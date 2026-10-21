using System.Windows;

namespace AccountingAutomationHub.Desktop.Views;

public partial class BalanceClosingEntryWindow : Window
{
    public BalanceClosingEntryWindow(string ledgerName, decimal closingBalance, DateTime defaultDate, IReadOnlyList<string> cashLedgers, IReadOnlyList<string> allLedgers)
    {
        InitializeComponent();
        LedgerName = ledgerName;
        ClosingBalance = closingBalance;
        LedgerText.Text = ledgerName;
        BalanceText.Text = $"Current closing balance: {Math.Abs(closingBalance):N2} {(closingBalance >= 0m ? "Dr" : "Cr")}";
        EntryHintText.Text = closingBalance > 0m ? "Closing requires CREDIT" : "Closing requires DEBIT";
        EntryDatePicker.SelectedDate = defaultDate.Date;
        CashLedgerBox.ItemsSource = cashLedgers;
        if (cashLedgers.Count == 1) CashLedgerBox.SelectedIndex = 0;
        if (cashLedgers.Count == 0)
        {
            CashMode.IsEnabled = false;
            CashMode.ToolTip = "No Cash-in-Hand Ledger is available in Master.";
            JournalMode.IsChecked = true;
        }
        OppositeLedgerBox.ItemsSource = allLedgers.Where(name => !name.Equals(ledgerName, StringComparison.OrdinalIgnoreCase)).ToArray();
        UpdateMode();
    }
    public string LedgerName { get; }
    public decimal ClosingBalance { get; }
    public string EntryMode => CashMode.IsChecked == true ? "Cash" : "Journal";
    public DateTime EntryDate => EntryDatePicker.SelectedDate?.Date ?? DateTime.Today;
    public string CashLedger => CashLedgerBox.SelectedItem?.ToString()?.Trim() ?? CashLedgerBox.Text?.Trim() ?? string.Empty;
    public string OppositeLedger => OppositeLedgerBox.Text?.Trim() ?? string.Empty;
    private void Mode_Changed(object sender, RoutedEventArgs e) => UpdateMode();
    private void UpdateMode()
    {
        if (CashPanel is null) return;
        CashPanel.Visibility = CashMode.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        JournalPanel.Visibility = JournalMode.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
    }
    private void Create_Click(object sender, RoutedEventArgs e)
    {
        if (EntryDatePicker.SelectedDate is null) { MessageBox.Show(this, "Select the closing-entry date.", "Closing Entry", MessageBoxButton.OK, MessageBoxImage.Information); return; }
        if (CashMode.IsChecked == true && string.IsNullOrWhiteSpace(CashLedger)) { MessageBox.Show(this, "Select the Cash Ledger.", "Closing Entry", MessageBoxButton.OK, MessageBoxImage.Information); return; }
        if (JournalMode.IsChecked == true && string.IsNullOrWhiteSpace(OppositeLedger)) { MessageBox.Show(this, "Select or enter the opposite Journal Ledger.", "Closing Entry", MessageBoxButton.OK, MessageBoxImage.Information); return; }
        if (JournalMode.IsChecked == true && OppositeLedger.Equals(LedgerName, StringComparison.OrdinalIgnoreCase)) { MessageBox.Show(this, "The opposite Ledger must be different from the Ledger being closed.", "Closing Entry", MessageBoxButton.OK, MessageBoxImage.Information); return; }
        DialogResult = true;
    }
}
