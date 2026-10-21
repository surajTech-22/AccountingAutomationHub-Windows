using System.Globalization;
using System.Windows;
using AccountingAutomationHub.Desktop.ViewModels;
using AccountingAutomationHub.Domain.Banking;
using AccountingAutomationHub.Domain.Governance;

namespace AccountingAutomationHub.Desktop.Views;

public partial class AdvancedReconciliationWindow : Window
{
    private readonly BankingViewModel _viewModel;
    private readonly IReadOnlyList<BankTransactionRecord> _transactions;
    private AdvancedReconciliationPreview? _preview;

    public AdvancedReconciliationWindow(BankingViewModel viewModel, IReadOnlyList<BankTransactionRecord> transactions)
    {
        InitializeComponent();
        _viewModel = viewModel;
        _transactions = transactions;
        TransactionGrid.ItemsSource = transactions;
        AdjustmentLedgerBox.ItemsSource = viewModel.MasterLedgerNames;
        BookAmountBox.Text = transactions.Sum(x => Math.Abs(x.Amount)).ToString("0.00", CultureInfo.InvariantCulture);
        SummaryText.Text = $"Selected {transactions.Count:N0} Bank Statement row(s), total {transactions.Sum(x => Math.Abs(x.Amount)):N2}.";
    }

    private void Preview_Click(object sender, RoutedEventArgs e)
    {
        if (!TryDecimal(BookAmountBox.Text, out var bookAmount) || !TryDecimal(AdjustmentBox.Text, out var adjustment))
        {
            MessageBox.Show(this, "Enter valid Book Amount and Adjustment Amount.", "Advanced Reconciliation", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        try
        {
            _preview = _viewModel.PreviewAdvancedReconciliation(_transactions, bookAmount, ReferencesBox.Text, adjustment, AdjustmentLedgerBox.Text ?? string.Empty);
            SummaryText.Text = $"{_preview.MatchType} • Bank {_preview.BankAmount:N2} • Book {_preview.BookAmount:N2} • Adjustment {_preview.AdjustmentAmount:N2} • Remaining {_preview.BankAmount - _preview.BookAmount - Math.Abs(_preview.AdjustmentAmount):N2}\n{_preview.Detail}";
            CommitButton.IsEnabled = Math.Abs(_preview.BankAmount - _preview.BookAmount - Math.Abs(_preview.AdjustmentAmount)) <= 0.005m;
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Advanced Reconciliation", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    private void Commit_Click(object sender, RoutedEventArgs e)
    {
        if (_preview is null) return;
        try
        {
            _viewModel.CommitAdvancedReconciliation(_preview);
            MessageBox.Show(this, $"Match {_preview.MatchId} committed.", "Advanced Reconciliation", MessageBoxButton.OK, MessageBoxImage.Information);
            DialogResult = true;
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Advanced Reconciliation", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
    private static bool TryDecimal(string? text,out decimal value) => decimal.TryParse(text, NumberStyles.Any, CultureInfo.CurrentCulture, out value) || decimal.TryParse(text, NumberStyles.Any, CultureInfo.InvariantCulture, out value);
}
