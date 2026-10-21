using System.Windows;
using System.Windows.Controls;

namespace AccountingAutomationHub.Desktop.Views;

public partial class AdvancedColumnFilterWindow : Window
{
    public AdvancedColumnFilterWindow(string columnName)
    {
        InitializeComponent();
        TitleText.Text = $"Filter: {columnName}";
    }
    public string FilterOperator { get; private set; } = "Contains";
    public string Value1 { get; private set; } = string.Empty;
    public string Value2 { get; private set; } = string.Empty;
    public bool ClearRequested { get; private set; }
    private void OperatorBox_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        SecondValuePanel.Visibility = SelectedOperator().Equals("Between", StringComparison.OrdinalIgnoreCase) ? Visibility.Visible : Visibility.Collapsed;
    private string SelectedOperator() => (OperatorBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "Contains";
    private void Clear_Click(object sender, RoutedEventArgs e) { ClearRequested = true; DialogResult = true; }
    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        FilterOperator = SelectedOperator();
        Value1 = Value1Box.Text?.Trim() ?? string.Empty;
        Value2 = Value2Box.Text?.Trim() ?? string.Empty;
        if (Value1.Length == 0) { MessageBox.Show(this, "Enter a filter value.", "Advanced Filter", MessageBoxButton.OK, MessageBoxImage.Information); return; }
        if (FilterOperator.Equals("Between", StringComparison.OrdinalIgnoreCase) && Value2.Length == 0) { MessageBox.Show(this, "Enter both values for Between.", "Advanced Filter", MessageBoxButton.OK, MessageBoxImage.Information); return; }
        DialogResult = true;
    }
}
