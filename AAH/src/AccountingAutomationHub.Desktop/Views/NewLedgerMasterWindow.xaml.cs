using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using AccountingAutomationHub.Domain.Masters;

namespace AccountingAutomationHub.Desktop.Views;

public partial class NewLedgerMasterWindow : Window
{
    private readonly IReadOnlyDictionary<string, MasterRecord> _groups;

    public NewLedgerMasterWindow(
        IEnumerable<MasterRecord> groups,
        string company,
        string? defaultGroup = null,
        MasterRecord? editRecord = null)
    {
        InitializeComponent();
        CompanyText.Text = company ?? string.Empty;
        _groups = groups
            .Where(group => !group.IsArchived &&
                            group.Type.Equals("Group", StringComparison.OrdinalIgnoreCase) &&
                            !group.Name.Trim().Equals("Primary", StringComparison.OrdinalIgnoreCase))
            .GroupBy(group => group.Name.Trim(), StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToDictionary(group => group.Name.Trim(), StringComparer.OrdinalIgnoreCase);

        foreach (var name in _groups.Keys.OrderBy(name => name, StringComparer.OrdinalIgnoreCase))
            GroupBox.Items.Add(name);

        var requestedGroup = editRecord?.Parent?.Trim();
        if (string.IsNullOrWhiteSpace(requestedGroup)) requestedGroup = defaultGroup?.Trim();
        if (!string.IsNullOrWhiteSpace(requestedGroup) && _groups.ContainsKey(requestedGroup))
            GroupBox.SelectedItem = _groups.Keys.First(name => name.Equals(requestedGroup, StringComparison.OrdinalIgnoreCase));
        else if (_groups.ContainsKey("Sundry Creditors"))
            GroupBox.SelectedItem = _groups.Keys.First(name => name.Equals("Sundry Creditors", StringComparison.OrdinalIgnoreCase));
        else if (GroupBox.Items.Count > 0)
            GroupBox.SelectedIndex = 0;

        if (editRecord is not null)
        {
            Title = "Edit Manual Ledger";
            HeaderText.Text = "Edit Manual Ledger";
            IntroText.Text = "Update this local Manual Ledger. Name changes are propagated through connected local accounting references; no Tally Master is altered by this screen.";
            ActionButton.Content = "Update Ledger";
            LedgerNameBox.Text = editRecord.Name;
            OpeningBalanceBox.Text = editRecord.OpeningBalance?.ToString("0.################", CultureInfo.CurrentCulture) ?? string.Empty;
        }

        Loaded += (_, _) =>
        {
            LedgerNameBox.Focus();
            LedgerNameBox.SelectAll();
        };
    }

    public string LedgerName => LedgerNameBox.Text.Trim();
    public string LedgerGroup => GroupBox.SelectedItem?.ToString()?.Trim() ?? string.Empty;
    public decimal? OpeningBalance { get; private set; }

    private void GroupBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var name = GroupBox.SelectedItem?.ToString()?.Trim() ?? string.Empty;
        ParentGroupText.Text = _groups.TryGetValue(name, out var group)
            ? (string.IsNullOrWhiteSpace(group.Parent) ? "Primary" : group.Parent.Trim())
            : string.Empty;
    }

    private void Create_Click(object sender, RoutedEventArgs e)
    {
        ValidationText.Text = string.Empty;
        if (LedgerName.Length == 0)
        {
            ValidationText.Text = "Enter the Ledger name.";
            LedgerNameBox.Focus();
            return;
        }

        if (LedgerGroup.Length == 0 || !_groups.ContainsKey(LedgerGroup))
        {
            ValidationText.Text = "Select an existing Ledger Group.";
            GroupBox.Focus();
            return;
        }

        var balanceText = OpeningBalanceBox.Text.Trim();
        if (balanceText.Length == 0)
        {
            OpeningBalance = null;
        }
        else if (decimal.TryParse(balanceText, NumberStyles.Number | NumberStyles.AllowLeadingSign, CultureInfo.CurrentCulture, out var balance) ||
                 decimal.TryParse(balanceText, NumberStyles.Number | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out balance))
        {
            OpeningBalance = balance;
        }
        else
        {
            ValidationText.Text = "Opening Balance must be a valid number or left blank.";
            OpeningBalanceBox.Focus();
            return;
        }

        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        DialogResult = false;
        e.Handled = true;
    }
}
