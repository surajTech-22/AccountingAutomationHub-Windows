using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using AccountingAutomationHub.Domain.Clients;

namespace AccountingAutomationHub.Desktop.Views;

/// <summary>
/// Phase-one Company Group dashboard: read-only same-PAN branch identity and offline
/// master XML preview. Never launches Tally, imports vouchers, or changes Client context.
/// </summary>
public sealed class CompanyGroupDashboardWindow : Window
{
    private readonly Func<string, CompanyGroupBranchPlan> _loadScope;
    private readonly ComboBox _financialYears = new() { MinWidth = 130, Margin = new Thickness(8, 0, 12, 0) };
    private readonly CheckBox _separateCash = new() { Content = "Separate branch Cash Ledgers", Margin = new Thickness(12, 0, 12, 0) };
    private readonly CheckBox _branchVoucherTypes = new() { Content = "Branch voucher type names", IsChecked = true, Margin = new Thickness(12, 0, 12, 0) };
    private readonly TextBlock _summary = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 12) };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBox _xml = new()
    {
        IsReadOnly = true,
        AcceptsReturn = true,
        AcceptsTab = true,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        FontFamily = new FontFamily("Consolas"), FontSize = 11,
        Text = "Select an FY and Preview Masters XML. Offline only; no Tally import is performed."
    };
    private readonly DataGrid _branches = new()
    {
        IsReadOnly = true,
        AutoGenerateColumns = false,
        CanUserAddRows = false,
        CanUserDeleteRows = false,
        SelectionMode = DataGridSelectionMode.Single,
        HeadersVisibility = DataGridHeadersVisibility.Column
    };
    private CompanyGroupBranchPlan? _currentPlan;

    public CompanyGroupDashboardWindow(
        string groupName, IReadOnlyList<string> financialYears,
        Func<string, CompanyGroupBranchPlan> loadScope)
    {
        _loadScope = loadScope;
        Title = "Company Group Dashboard - " + groupName;
        Width = 1160;
        Height = 725;
        MinWidth = 840;
        MinHeight = 540;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var layout = new Grid { Margin = new Thickness(18) };
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var heading = new StackPanel();
        heading.Children.Add(new TextBlock { Text = "Company Group: " + groupName, FontSize = 23, FontWeight = FontWeights.SemiBold });
        heading.Children.Add(new TextBlock
        {
            Text = "Same-PAN branch records / owner-wise accounting foundation. Source company data remains independent.",
            Foreground = Brushes.DimGray, Margin = new Thickness(0, 4, 0, 0)
        });
        Grid.SetRow(heading, 0);
        layout.Children.Add(heading);

        var controls = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 16, 0, 6) };
        controls.Children.Add(new TextBlock { Text = "Financial year", VerticalAlignment = VerticalAlignment.Center });
        foreach (var year in financialYears) _financialYears.Items.Add(year);
        controls.Children.Add(_financialYears);
        controls.Children.Add(_separateCash);
        controls.Children.Add(_branchVoucherTypes);
        Grid.SetRow(controls, 1);
        layout.Children.Add(controls);

        var middle = new Grid();
        middle.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.15, GridUnitType.Star) });
        middle.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
        middle.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        middle.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        middle.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        Grid.SetColumnSpan(_summary, 3);
        Grid.SetRow(_summary, 0);
        middle.Children.Add(_summary);

        _branches.Columns.Add(new DataGridTextColumn { Header = "Client Code", Binding = new Binding(nameof(CompanyGroupBranchRegistration.ClientCode)), Width = new DataGridLength(105) });
        _branches.Columns.Add(new DataGridTextColumn { Header = "Branch", Binding = new Binding(nameof(CompanyGroupBranchRegistration.BranchName)), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
        _branches.Columns.Add(new DataGridTextColumn { Header = "GSTIN", Binding = new Binding(nameof(CompanyGroupBranchRegistration.Gstin)), Width = new DataGridLength(150) });
        _branches.Columns.Add(new DataGridTextColumn { Header = "PAN", Binding = new Binding(nameof(CompanyGroupBranchRegistration.Pan)), Width = new DataGridLength(110) });
        Grid.SetRow(_branches, 1);
        Grid.SetColumn(_branches, 0);
        middle.Children.Add(_branches);

        var right = new DockPanel();
        var rightTitle = new TextBlock
        {
            Text = "Offline Tally Masters XML Preview (never sent)",
            FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 6)
        };
        DockPanel.SetDock(rightTitle, Dock.Top);
        right.Children.Add(rightTitle);
        right.Children.Add(_xml);
        Grid.SetRow(right, 1);
        Grid.SetColumn(right, 2);
        middle.Children.Add(right);
        Grid.SetRow(middle, 2);
        layout.Children.Add(middle);

        var footer = new DockPanel { Margin = new Thickness(0, 12, 0, 0) };
        var button = new Button
        {
            Content = "Preview Masters XML", Padding = new Thickness(14, 8, 14, 8),
            MinWidth = 175, Margin = new Thickness(0, 0, 12, 0)
        };
        button.Click += (_, _) => BuildPreview();
        DockPanel.SetDock(button, Dock.Left);
        footer.Children.Add(button);
        var close = new Button { Content = "Close", Padding = new Thickness(15, 8, 15, 8) };
        close.Click += (_, _) => Close();
        DockPanel.SetDock(close, Dock.Right);
        footer.Children.Add(close);
        footer.Children.Add(_status);
        Grid.SetRow(footer, 3);
        layout.Children.Add(footer);
        Content = layout;

        _financialYears.SelectionChanged += (_, _) => LoadBranchScope();
        if (_financialYears.Items.Count > 0)
            _financialYears.SelectedIndex = 0;
        else
            _status.Text = "This Company Group has no accessible active Client/FY members. No XML was generated.";
    }

    private void LoadBranchScope()
    {
        _currentPlan = null;
        _branches.ItemsSource = null;
        _xml.Text = string.Empty;
        try
        {
            if (_financialYears.SelectedItem is not string year) return;
            _currentPlan = _loadScope(year);
            _branches.ItemsSource = _currentPlan.Branches;
            _summary.Text = $"FY {year} | PAN {_currentPlan.Pan} | {_currentPlan.ClientCount} source client(s) | {_currentPlan.GstinCount} GST registration/branch(es)\n" +
                            "Cash Contra uses branch types when enabled; bank-to-bank transfer uses one common Contra. Branch-ledger groups retain Tally's normal accounting parents.";
            _status.Text = "Ready for offline master preview. LIVE Tally import is disabled until destination/master identity and voucher readback verification are implemented.";
        }
        catch (InvalidOperationException exception)
        {
            _summary.Text = "Group scope needs review: " + exception.Message;
            _status.Text = "No masters or vouchers are imported. Different PAN owner businesses require a separate grouping stage.";
        }
    }

    private void BuildPreview()
    {
        if (_currentPlan is null)
        {
            _status.Text = "No validated same-PAN branch plan available.";
            return;
        }
        _xml.Text = _currentPlan.BuildOfflineMastersPreviewXml(_separateCash.IsChecked == true);
        var branch = _currentPlan.Branches[0].BranchName;
        _status.Text = "Preview only. Sample Sales type: " +
                       _currentPlan.VoucherTypeFor(branch, "Sales", _branchVoucherTypes.IsChecked == true) +
                       "; Cash Contra: " + _currentPlan.VoucherTypeFor(branch, "Cash Contra", _branchVoucherTypes.IsChecked == true) +
                       "; Interbank transfer: Contra. No Tally write performed.";
    }
}
