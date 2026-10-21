using System.Collections.ObjectModel;
using System.Windows;

namespace AccountingAutomationHub.Desktop.Views;

public partial class RecurringDateEditorWindow : Window
{
    public ObservableCollection<DateTime> Dates { get; }
    public DateTime? SelectedDate { get; set; }

    private readonly DateTime _minimumDate;
    private readonly DateTime _maximumDate;

    public RecurringDateEditorWindow(IEnumerable<DateTime> dates, DateTime minimumDate, DateTime maximumDate)
    {
        if (maximumDate.Date < minimumDate.Date) throw new ArgumentOutOfRangeException(nameof(maximumDate));
        InitializeComponent();
        _minimumDate = minimumDate.Date;
        _maximumDate = maximumDate.Date;
        Dates = new ObservableCollection<DateTime>(dates.Select(value => value.Date)
            .Where(value => value >= _minimumDate && value <= _maximumDate)
            .Distinct().OrderBy(value => value));
        AddDatePicker.DisplayDateStart = _minimumDate;
        AddDatePicker.DisplayDateEnd = _maximumDate;
        DataContext = this;
    }

    private void AddDate_Click(object sender, RoutedEventArgs e)
    {
        if (AddDatePicker.SelectedDate is not DateTime date) return;
        date = date.Date;
        if (date < _minimumDate || date > _maximumDate)
        {
            MessageBox.Show(this, "The selected date must remain inside the current Financial Year.", "Recurring Dates", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (!Dates.Contains(date)) Dates.Add(date);
        SortDates();
    }

    private void DeleteDate_Click(object sender, RoutedEventArgs e)
    {
        if (DatesGrid.SelectedItem is DateTime date) Dates.Remove(date);
    }

    private void UseDates_Click(object sender, RoutedEventArgs e)
    {
        if (Dates.Count == 0)
        {
            MessageBox.Show(this, "Keep at least one recurring date.", "Recurring Dates", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        SortDates();
        DialogResult = true;
    }

    private void SortDates()
    {
        var ordered = Dates.OrderBy(value => value).ToArray();
        Dates.Clear();
        foreach (var date in ordered) Dates.Add(date);
    }
}
