using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace AccountingAutomationHub.Desktop.Services;

public static class PasswordPromptService
{
    public static string? Prompt(string fileName)
    {
        var window = new Window
        {
            Title = "Protected Bank Statement",
            Width = 440,
            Height = 210,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ResizeMode = ResizeMode.NoResize,
            Owner = System.Windows.Application.Current?.MainWindow,
            Background = Brushes.White
        };

        var root = new Grid { Margin = new Thickness(20) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.Children.Add(new TextBlock
        {
            Text = $"Enter the password for:\n{fileName}",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 12)
        });

        var passwordBox = new PasswordBox { MinWidth = 360, Margin = new Thickness(0, 0, 0, 16) };
        Grid.SetRow(passwordBox, 1);
        root.Children.Add(passwordBox);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var ok = new Button { Content = "Open", Width = 90, IsDefault = true, Margin = new Thickness(0, 0, 8, 0) };
        var cancel = new Button { Content = "Skip", Width = 90, IsCancel = true };
        ok.Click += (_, _) => { window.DialogResult = true; window.Close(); };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        Grid.SetRow(buttons, 2);
        root.Children.Add(buttons);
        window.Content = root;
        window.Loaded += (_, _) => passwordBox.Focus();

        return window.ShowDialog() == true ? passwordBox.Password : null;
    }
}
