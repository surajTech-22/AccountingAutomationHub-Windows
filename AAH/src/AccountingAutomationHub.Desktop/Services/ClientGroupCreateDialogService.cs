using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using AccountingAutomationHub.Application.Settings;
using AccountingAutomationHub.Desktop.ViewModels;

namespace AccountingAutomationHub.Desktop.Services;

public static class ClientGroupCreateDialogService
{
    public static string? Show(IApplicationPreferenceRepository preferences, string? parentId, Window? owner)
    {
        var data = ClientGroupingCatalog.Load(preferences.Get(ClientGroupingCatalog.PreferenceKey));
        var normalizedParent = (parentId ?? string.Empty).Trim();
        if (normalizedParent.Length > 0 && data.Folders.All(x => !x.Id.Equals(normalizedParent, StringComparison.OrdinalIgnoreCase)))
            normalizedParent = string.Empty;

        var caProfiles = new List<CaProfileRow>();
        try { caProfiles = JsonSerializer.Deserialize<List<CaProfileRow>>(preferences.Get(SettingsViewModel.CaProfilesPreferenceKey)) ?? []; }
        catch { }

        var window = new Window
        {
            Title = "Make Group",
            Width = 470,
            Height = 370,
            MinWidth = 430,
            MinHeight = 340,
            WindowStartupLocation = owner is null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner,
            Owner = owner,
            ResizeMode = ResizeMode.NoResize
        };
        var root = new Grid { Margin = new Thickness(18) };
        for (var i = 0; i < 7; i++) root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var parentPath = normalizedParent.Length == 0 ? "Top level" : BuildPath(data, normalizedParent);
        var title = new TextBlock { Text = "Create New Group", FontSize = 19, FontWeight = FontWeights.SemiBold };
        root.Children.Add(title);
        var parentText = new TextBlock { Text = "Destination: " + parentPath, Margin = new Thickness(0, 5, 0, 12), TextWrapping = TextWrapping.Wrap };
        Grid.SetRow(parentText, 1); root.Children.Add(parentText);

        var typeLabel = new TextBlock { Text = "Group Type", Margin = new Thickness(0, 0, 0, 4) };
        Grid.SetRow(typeLabel, 2); root.Children.Add(typeLabel);
        var typeBox = new ComboBox { ItemsSource = ClientGroupFolderTypes.CreatableTypes, Padding = new Thickness(7,5,7,5) };
        Grid.SetRow(typeBox, 3); root.Children.Add(typeBox);

        var namePanel = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };
        namePanel.Children.Add(new TextBlock { Text = "Group Name", Margin = new Thickness(0,0,0,4) });
        var nameBox = new TextBox { Padding = new Thickness(8,6,8,6) };
        namePanel.Children.Add(nameBox);
        Grid.SetRow(namePanel, 4); root.Children.Add(namePanel);

        var caPanel = new StackPanel { Margin = new Thickness(0,12,0,0), Visibility = Visibility.Collapsed };
        caPanel.Children.Add(new TextBlock { Text = "Linked CA", Margin = new Thickness(0,0,0,4) });
        var caBox = new ComboBox { ItemsSource = caProfiles, DisplayMemberPath = "CaName", Padding = new Thickness(7,5,7,5) };
        caPanel.Children.Add(caBox);
        Grid.SetRow(caPanel, 5); root.Children.Add(caPanel);

        var error = new TextBlock { Foreground = System.Windows.Media.Brushes.Firebrick, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0,10,0,0) };
        Grid.SetRow(error, 6); root.Children.Add(error);

        typeBox.SelectionChanged += (_, _) =>
        {
            var isCa = string.Equals(typeBox.SelectedItem?.ToString(), ClientGroupFolderTypes.Ca, StringComparison.OrdinalIgnoreCase);
            caPanel.Visibility = isCa ? Visibility.Visible : Visibility.Collapsed;
            namePanel.Visibility = isCa ? Visibility.Collapsed : Visibility.Visible;
        };

        string? createdId = null;
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0,16,0,0) };
        var cancel = new Button { Content = "Cancel", Padding = new Thickness(14,7,14,7), Margin = new Thickness(0,0,8,0), IsCancel = true };
        var create = new Button { Content = "Create", Padding = new Thickness(14,7,14,7), IsDefault = true };
        buttons.Children.Add(cancel); buttons.Children.Add(create);
        Grid.SetRow(buttons, 8); root.Children.Add(buttons);

        create.Click += (_, _) =>
        {
            var typeText = typeBox.SelectedItem?.ToString() ?? string.Empty;
            if (!ClientGroupFolderTypes.CreatableTypes.Contains(typeText, StringComparer.OrdinalIgnoreCase))
            { error.Text = "Select a Group Type first."; return; }
            var type = ClientGroupFolderTypes.Normalize(typeText);
            var selectedCa = caBox.SelectedItem as CaProfileRow;
            if (type.Equals(ClientGroupFolderTypes.Ca, StringComparison.OrdinalIgnoreCase) && selectedCa is null)
            { error.Text = "Select the saved CA first."; return; }
            var name = type.Equals(ClientGroupFolderTypes.Ca, StringComparison.OrdinalIgnoreCase) ? selectedCa!.CaName.Trim() : nameBox.Text.Trim();
            if (name.Length == 0) { error.Text = "Enter a Group Name first."; return; }
            var effectiveParent = type.Equals(ClientGroupFolderTypes.Firm, StringComparison.OrdinalIgnoreCase) ? string.Empty : normalizedParent;
            if (data.Folders.Any(x => x.ParentId.Equals(effectiveParent, StringComparison.OrdinalIgnoreCase) && x.Name.Equals(name, StringComparison.OrdinalIgnoreCase) && x.Type.Equals(type, StringComparison.OrdinalIgnoreCase)))
            { error.Text = "That Group already exists at this menu level."; return; }
            var record = new ClientGroupFolderRecord
            {
                Name = name,
                Type = type,
                ParentId = effectiveParent,
                LinkedCaProfileId = type.Equals(ClientGroupFolderTypes.Ca, StringComparison.OrdinalIgnoreCase) ? selectedCa?.ProfileId ?? string.Empty : string.Empty
            };
            data.Folders.Add(record);
            preferences.Set(ClientGroupingCatalog.PreferenceKey, ClientGroupingCatalog.Save(data));
            createdId = record.Id;
            window.DialogResult = true;
            window.Close();
        };
        cancel.Click += (_, _) => window.Close();
        window.Content = root;
        typeBox.SelectedItem = ClientGroupFolderTypes.CompanyGroup;
        return window.ShowDialog() == true ? createdId : null;
    }

    public static string BuildPath(ClientGroupCatalogData data, string folderId)
    {
        var byId = data.Folders.ToDictionary(x => x.Id, StringComparer.OrdinalIgnoreCase);
        if (!byId.TryGetValue(folderId, out var current)) return string.Empty;
        var names = new List<string>();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (current is not null && visited.Add(current.Id))
        {
            names.Add(current.Name);
            if (string.IsNullOrWhiteSpace(current.ParentId) || !byId.TryGetValue(current.ParentId, out current)) break;
        }
        names.Reverse();
        return string.Join(" > ", names);
    }
}
