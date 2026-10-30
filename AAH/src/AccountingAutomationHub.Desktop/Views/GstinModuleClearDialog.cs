using System.Windows;
using System.Windows.Controls;
using AccountingAutomationHub.Domain.Clients;

namespace AccountingAutomationHub.Desktop.Views;

/// <summary>
/// Reuses the existing Sales/Purchase module. For clients with multiple
/// registrations, a destructive clear must explicitly name one GSTIN.
/// </summary>
internal static class GstinModuleClearDialog
{
    public static string? Select(ClientProfile client, string module)
    {
        var items = new[] { client.Gstin }
            .Concat(client.AdditionalGstins ?? [])
            .Select(value => value.Trim().ToUpperInvariant())
            .Where(value => value.Length == 15)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (items.Length < 2) return items.FirstOrDefault();

        var window = new Window
        {
            Title = $"Clear {module} - Select GSTIN",
            Width = 470,
            Height = 230,
            MinWidth = 410,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false
        };
        if (System.Windows.Application.Current?.MainWindow is { } owner && !ReferenceEquals(owner, window))
            window.Owner = owner;
        else
            window.WindowStartupLocation = WindowStartupLocation.CenterScreen;

        var stack = new StackPanel { Margin = new Thickness(20) };
        stack.Children.Add(new TextBlock
        {
            Text = "Select the exact GSTIN to clear. All other registration records will be preserved.",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 12)
        });
        var picker = new ComboBox
        {
            ItemsSource = items,
            SelectedIndex = -1,
            Height = 32,
            Margin = new Thickness(0, 0, 0, 14)
        };
        stack.Children.Add(picker);
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right
        };
        var cancel = new Button { Content = "Cancel", Width = 90, Height = 31, Margin = new Thickness(0, 0, 8, 0) };
        var confirm = new Button { Content = "Continue", Width = 94, Height = 31, IsEnabled = false };
        cancel.Click += (_, _) => window.DialogResult = false;
        picker.SelectionChanged += (_, _) => confirm.IsEnabled = picker.SelectedItem is string;
        confirm.Click += (_, _) => window.DialogResult = true;
        buttons.Children.Add(cancel);
        buttons.Children.Add(confirm);
        stack.Children.Add(buttons);
        window.Content = stack;
        return window.ShowDialog() == true ? picker.SelectedItem as string : null;
    }
}
