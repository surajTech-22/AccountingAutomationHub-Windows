using System.Collections.Generic;
using System.Linq;
using System.Windows;
using AccountingAutomationHub.Domain.Audit;

namespace AccountingAutomationHub.Desktop.Views;

public partial class AuditBankStatementAccountPickerWindow : Window
{
    private readonly IReadOnlyList<AuditBankAccountChoice> _choices;

    public AuditBankStatementAccountPickerWindow(
        AuditBankStatementImportPreview preview,
        IReadOnlyList<AuditBankAccountChoice> choices)
    {
        InitializeComponent();
        _choices = choices;
        SourceText.Text = $"{preview.SourceFile} · {preview.SourceSheet}";
        EvidenceText.Text = $"Detected account: {(string.IsNullOrWhiteSpace(preview.DetectedAccountIdentity) ? "Not found in source" : preview.DetectedAccountIdentity)}\n" +
                            $"Period: {preview.PeriodFrom:dd-MM-yyyy} to {preview.PeriodTo:dd-MM-yyyy} · Rows: {preview.Transactions.Count:N0}\n" +
                            $"Debit: {preview.TotalDebit:N2} · Credit: {preview.TotalCredit:N2} · {preview.ParseStatus}";
        AccountBox.ItemsSource = _choices;
        if (_choices.Count == 1) AccountBox.SelectedIndex = 0;
        else
        {
            var detected = (preview.DetectedAccountIdentity ?? string.Empty).Replace(" ", string.Empty, System.StringComparison.Ordinal);
            if (detected.Length > 0)
            {
                var candidate = _choices.FirstOrDefault(choice =>
                    choice.Name.Replace(" ", string.Empty, System.StringComparison.Ordinal).Contains(detected, System.StringComparison.OrdinalIgnoreCase) ||
                    detected.Contains(choice.Name.Replace(" ", string.Empty, System.StringComparison.Ordinal), System.StringComparison.OrdinalIgnoreCase));
                if (candidate is not null) AccountBox.SelectedItem = candidate;
            }
        }
    }

    public AuditBankAccountChoice? SelectedChoice => AccountBox.SelectedItem as AuditBankAccountChoice;

    private void Import_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedChoice is null)
        {
            MessageBox.Show(this, "Select the Tally Bank/OD account that owns this statement.", "Audit Bank Statement", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}

public sealed class AuditBankAccountChoice
{
    public string Name { get; init; } = string.Empty;
    public string Category { get; init; } = "Bank";
    public string MasterId { get; init; } = string.Empty;
    public string DisplayText => MasterId.Length > 0 ? $"{Category} · {Name} · MID {MasterId}" : $"{Category} · {Name}";
}
