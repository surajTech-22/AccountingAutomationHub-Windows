using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using AccountingAutomationHub.Desktop.Services;

namespace AccountingAutomationHub.Desktop.Views;

public partial class AdvancedCloneWindow : Window
{
    private readonly long _clientId;
    private readonly string _financialYear;
    private readonly ClientSchedulePreferenceService _preferences;
    private readonly IReadOnlyList<DateTime> _manualHolidays;

    public AdvancedCloneWindow(long clientId, string financialYear, decimal sourceAmount, DateTime defaultStartDate, ApplicationPreferenceService settings)
    {
        InitializeComponent();
        _clientId = clientId;
        _financialYear = financialYear;
        _preferences = new ClientSchedulePreferenceService(settings);
        _manualHolidays = _preferences.GetManualHolidays(clientId, financialYear);
        AmountBox.Text = Math.Abs(sourceAmount).ToString("0.00", CultureInfo.InvariantCulture);
        StartDatePicker.SelectedDate = defaultStartDate.Date;
        EndDatePicker.SelectedDate = FinancialYearEnd(financialYear);
        OverrideCheck.IsChecked = _preferences.GetOverrideAdvancedRules(clientId, financialYear);
        HolidaySummary.Text = _manualHolidays.Count == 0
            ? "Automatic national holidays only. Add client-specific manual holidays under Settings → Notifications & Holidays."
            : $"{_manualHolidays.Count:N0} client-specific manual holiday(s): {string.Join(", ", _manualHolidays.Take(8).Select(date => date.ToString("dd-MMM-yyyy")))}{(_manualHolidays.Count > 8 ? " …" : string.Empty)}";
        UpdateMode();
    }

    public AdvancedCloneConfiguration? Configuration { get; private set; }
    public IReadOnlyList<CloneScheduleEntry> Schedule { get; private set; } = [];

    private void Mode_Changed(object sender, RoutedEventArgs e) => UpdateMode();

    private void SchedulingMode_Checked(object sender, RoutedEventArgs e)
    {
        if (sender == EveryNCheck && EveryNCheck.IsChecked == true) DistributionCheck.IsChecked = false;
        if (sender == DistributionCheck && DistributionCheck.IsChecked == true) EveryNCheck.IsChecked = false;
        AdvancedRule_Changed(sender, e);
    }

    private void AdvancedRule_Changed(object? sender, EventArgs e)
    {
        if (!IsLoaded || SpecificMode.IsChecked != true || DistributionCheck.IsChecked != true || OverrideCheck.IsChecked == true) return;
        if (StartDatePicker.SelectedDate is not DateTime start || EndDatePicker.SelectedDate is not DateTime end) return;
        var maximum = AdvancedCloneScheduleService.MaximumTransactionsPerPeriod(start, end, SelectedText(DistributionPeriodBox, "Monthly"), _manualHolidays);
        if (maximum <= 0) return;
        if (int.TryParse(PerPeriodBox.Text, out var entered) && entered > maximum)
        {
            PerPeriodBox.Text = maximum.ToString(CultureInfo.InvariantCulture);
            PerPeriodBox.CaretIndex = PerPeriodBox.Text.Length;
            ValidationText.Text = $"Adjusted to {maximum:N0}: that is the maximum unique working-date capacity in at least one selected {SelectedText(DistributionPeriodBox, "Monthly").ToLowerInvariant()} period. Enable Override Advanced Rules for this client to exceed it.";
        }
    }

    private void UpdateMode()
    {
        if (!IsLoaded && AdvancedPanel is null) return;
        var specific = SpecificMode.IsChecked == true;
        AdvancedPanel.Visibility = specific ? Visibility.Visible : Visibility.Collapsed;
        OverridePanel.Visibility = specific ? Visibility.Visible : Visibility.Collapsed;
        AmountLabel.Text = specific ? "Amount per clone" : "Bulk total amount";
        AmountHelp.Text = specific
            ? "Every generated voucher uses this exact amount."
            : "The total is divided across the generated basic-frequency dates; the final voucher takes the paise rounding difference.";
    }

    private void Generate_Click(object sender, RoutedEventArgs e)
    {
        ValidationText.Text = string.Empty;
        if (!TryDecimal(AmountBox.Text, out var amount) || amount <= 0m)
        {
            ValidationText.Text = "Enter a valid amount greater than zero.";
            return;
        }
        if (StartDatePicker.SelectedDate is not DateTime start || EndDatePicker.SelectedDate is not DateTime end)
        {
            ValidationText.Text = "Select both Start Date and End Date.";
            return;
        }
        var config = new AdvancedCloneConfiguration
        {
            AmountMode = SpecificMode.IsChecked == true ? "Specific Amount" : "Bulk Amount Divide",
            Amount = amount,
            StartDate = start.Date,
            EndDate = end.Date,
            BasicFrequency = SelectedText(FrequencyBox, "Monthly"),
            UseEveryNDays = SpecificMode.IsChecked == true && EveryNCheck.IsChecked == true,
            EveryNDays = ParseInt(EveryNBox.Text, 1),
            UseTotalTransactions = SpecificMode.IsChecked == true && TotalCheck.IsChecked == true,
            TotalTransactions = ParseInt(TotalBox.Text, 1),
            UseDistribution = SpecificMode.IsChecked == true && DistributionCheck.IsChecked == true,
            DistributionPeriod = SelectedText(DistributionPeriodBox, "Monthly"),
            TransactionsPerPeriod = ParseInt(PerPeriodBox.Text, 1),
            OverrideAdvancedRules = SpecificMode.IsChecked == true && OverrideCheck.IsChecked == true,
            ManualHolidays = _manualHolidays
        };
        try
        {
            var schedule = AdvancedCloneScheduleService.Generate(config);
            if (config.OverrideAdvancedRules)
            {
                var answer = MessageBox.Show(this,
                    $"Override Advanced Rules is enabled for this client.\n\nAAH will generate {schedule.Count:N0} voucher(s) and may reuse a working date only where the requested distribution cannot fit unique dates.\n\nContinue?",
                    "Override Advanced Rules", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
                if (answer != MessageBoxResult.Yes) return;
            }
            _preferences.SetOverrideAdvancedRules(_clientId, _financialYear, config.OverrideAdvancedRules);
            Configuration = config;
            Schedule = schedule;
            DialogResult = true;
        }
        catch (Exception ex)
        {
            ValidationText.Text = ex.Message;
        }
    }

    private static bool TryDecimal(string text, out decimal value) =>
        decimal.TryParse((text ?? string.Empty).Replace(",", string.Empty), NumberStyles.Number, CultureInfo.InvariantCulture, out value) ||
        decimal.TryParse(text, NumberStyles.Number, CultureInfo.CurrentCulture, out value);
    private static int ParseInt(string text, int fallback) => int.TryParse(text, out var value) ? Math.Max(1, value) : fallback;
    private static string SelectedText(ComboBox box, string fallback) => (box.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? fallback;
    private static DateTime FinancialYearEnd(string financialYear)
    {
        var first = financialYear.Split('-', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
        return int.TryParse(first, out var year) ? new DateTime(year + 1, 3, 31) : DateTime.Today.AddYears(1);
    }
}
