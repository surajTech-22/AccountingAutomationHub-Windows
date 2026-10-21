using System.Windows;
using System.Windows.Controls;

namespace AccountingAutomationHub.Desktop.Views;

public partial class RecurringSetupWindow : Window
{
    public RecurringSetupWindow(string defaultName, DateTime defaultStartDate)
    {
        InitializeComponent();
        ProfileNameBox.Text = defaultName;
        StartDatePicker.SelectedDate = defaultStartDate.Date;
    }

    public string ProfileName => ProfileNameBox.Text.Trim();
    public string Frequency => (FrequencyBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "Monthly";
    public DateTime StartDate => StartDatePicker.SelectedDate?.Date ?? DateTime.Today;
    public DateTime? EndDate => EndDatePicker.SelectedDate?.Date;

    private void Create_Click(object sender, RoutedEventArgs e)
    {
        if (StartDatePicker.SelectedDate is null)
        {
            MessageBox.Show(this, "Select the first run date.", "Recurring Transaction", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (EndDatePicker.SelectedDate is DateTime end && end.Date < StartDate)
        {
            MessageBox.Show(this, "End Date cannot be before First Run Date.", "Recurring Transaction", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        DialogResult = true;
    }
}
