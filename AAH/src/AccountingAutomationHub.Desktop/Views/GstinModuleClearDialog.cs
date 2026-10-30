using System.Windows;
using System.Windows.Controls;
using AccountingAutomationHub.Domain.Clients;

namespace AccountingAutomationHub.Desktop.Views;

/// <summary>
/// Multi-registration actions share the existing Sales/Purchase modules.
/// Clear requires explicit RETAIN choices; reimport requires explicit source choices.
/// No selection can silently mutate an accounting registration.
/// </summary>
internal static class GstinModuleClearDialog
{
    public static IReadOnlyList<string>? SelectToClear(ClientProfile client, string module)
    {
        var registrations = Registrations(client);
        if (registrations.Length < 2) return null;
        var retained = ShowCheckboxDialog(
            registrations,
            $"Clear {module} - Retain GSTINs",
            "Tick the GSTINs to RETAIN. Untick only those whose saved " +
            $"{module} records you want to clear. No source files or other modules will be deleted.",
            "Continue",
            initiallyChecked: true);
        if (retained is null) return null;
        // No destructive action if nothing was unselected.
        return registrations.Except(retained, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public static IReadOnlyList<string>? SelectForReimport(ClientProfile client, string module)
    {
        var registrations = Registrations(client);
        if (registrations.Length < 2) return registrations;
        return ShowCheckboxDialog(
            registrations,
            $"Reimport {module} - Choose GSTINs",
            $"Select the GSTINs to reimport using the existing {module} import method. " +
            "Unselected registrations and their saved records are left unchanged.",
            "Reimport selected",
            initiallyChecked: false);
    }

    private static string[] Registrations(ClientProfile client) =>
        new[] { client.Gstin }.Concat(client.AdditionalGstins ?? [])
            .Select(value => value.Trim().ToUpperInvariant())
            .Where(value => value.Length == 15)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static IReadOnlyList<string>? ShowCheckboxDialog(
        IReadOnlyList<string> registrations,
        string title,
        string instructions,
        string action,
        bool initiallyChecked)
    {
        var window = new Window
        {
            Title = title,
            Width = 530,
            Height = Math.Min(530, Math.Max(280, 205 + registrations.Count * 38)),
            MinWidth = 440,
            ResizeMode = ResizeMode.CanResize,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            ShowInTaskbar = false
        };
        if (System.Windows.Application.Current?.MainWindow is { } owner &&
            !ReferenceEquals(owner, window))
        {
            window.Owner = owner;
            window.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        }

        var root = new DockPanel { Margin = new Thickness(20) };
        var footer = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 12, 0, 0)
        };
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(footer);
        var explanation = new TextBlock
        {
            Text = instructions,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 12)
        };
        DockPanel.SetDock(explanation, Dock.Top);
        root.Children.Add(explanation);

        var selected = new List<(string Gstin, CheckBox Check)>();
        var choices = new StackPanel();
        foreach (var gstin in registrations)
        {
            var checkbox = new CheckBox
            {
                Content = gstin,
                IsChecked = initiallyChecked,
                Margin = new Thickness(0, 6, 0, 6),
                FontSize = 14
            };
            selected.Add((gstin, checkbox));
            choices.Children.Add(checkbox);
        }
        root.Children.Add(new ScrollViewer
        {
            Content = choices,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        });
        var cancel = new Button
        {
            Content = "Cancel", Width = 90, Height = 32,
            Margin = new Thickness(0, 0, 8, 0),
            IsCancel = true
        };
        var confirm = new Button { Content = action, MinWidth = 148, Height = 32 };
        void RefreshAction()
        {
            var count = selected.Count(item => item.Check.IsChecked == true);
            confirm.IsEnabled = initiallyChecked ? count < registrations.Count : count > 0;
        }
        foreach (var (_, checkbox) in selected)
        {
            checkbox.Checked += (_, _) => RefreshAction();
            checkbox.Unchecked += (_, _) => RefreshAction();
        }
        cancel.Click += (_, _) => window.DialogResult = false;
        confirm.Click += (_, _) => window.DialogResult = true;
        footer.Children.Add(cancel);
        footer.Children.Add(confirm);
        RefreshAction();
        window.Content = root;
        return window.ShowDialog() == true
            ? selected.Where(item => item.Check.IsChecked == true)
                .Select(item => item.Gstin).ToArray()
            : null;
    }
}
