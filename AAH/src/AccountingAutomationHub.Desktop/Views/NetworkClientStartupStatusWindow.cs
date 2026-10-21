using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace AccountingAutomationHub.Desktop.Views;

/// <summary>
/// Small non-login startup surface shown while the LAN client performs server/session/snapshot
/// checks. It prevents a valid but slow startup from looking like "nothing happened" and does not
/// expose enrollment/OTP controls on a trusted workstation.
/// </summary>
public sealed class NetworkClientStartupStatusWindow : Window
{
    private readonly TextBlock _status;

    public NetworkClientStartupStatusWindow()
    {
        Title = "Accounting Automation Hub LAN";
        Width = 460;
        Height = 190;
        MinWidth = 400;
        MinHeight = 170;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = true;
        Topmost = false;

        var root = new Grid { Margin = new Thickness(24) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(14) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(16) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var heading = new TextBlock
        {
            Text = "Opening Accounting Automation Hub LAN",
            FontSize = 19,
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap
        };
        Grid.SetRow(heading, 0);
        root.Children.Add(heading);

        _status = new TextBlock
        {
            Text = "Preparing the LAN application...",
            FontSize = 13,
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.82
        };
        Grid.SetRow(_status, 2);
        root.Children.Add(_status);

        var progress = new ProgressBar
        {
            IsIndeterminate = true,
            Height = 7,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        Grid.SetRow(progress, 4);
        root.Children.Add(progress);

        Content = root;
    }

    public void SetStatus(string value)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(() => SetStatus(value));
            return;
        }
        _status.Text = string.IsNullOrWhiteSpace(value) ? "Opening AAH LAN..." : value.Trim();
    }
}
