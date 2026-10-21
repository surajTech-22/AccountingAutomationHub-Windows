using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace AccountingAutomationHub.Desktop.Services;

/// <summary>
/// Non-modal click-to-open information surface used by compact i/info buttons.
/// It never captures the application like MessageBox/Popup StaysOpen=False and closes
/// as soon as the pointer leaves both the source control and the information card.
/// </summary>
public static class TransientInfoPopupService
{
    private static Popup? _activePopup;
    private static FrameworkElement? _activeTarget;
    private static Border? _activeCard;
    private static DispatcherTimer? _closeTimer;
    private static MouseEventHandler? _targetLeaveHandler;
    private static MouseEventHandler? _cardLeaveHandler;
    private static RoutedEventHandler? _targetUnloadedHandler;

    public static void Show(FrameworkElement target, string title, string message, double width = 470d)
    {
        if (target is null || string.IsNullOrWhiteSpace(message)) return;
        Close();

        var background = target.TryFindResource("SurfaceBrush") as Brush ?? SystemColors.WindowBrush;
        var foreground = target.TryFindResource("TextPrimaryBrush") as Brush ?? SystemColors.WindowTextBrush;
        var secondary = target.TryFindResource("TextSecondaryBrush") as Brush ?? SystemColors.GrayTextBrush;
        var borderBrush = target.TryFindResource("BorderBrush") as Brush ?? SystemColors.ActiveBorderBrush;

        var stack = new StackPanel();
        if (!string.IsNullOrWhiteSpace(title))
        {
            stack.Children.Add(new TextBlock
            {
                Text = title.Trim(),
                FontWeight = FontWeights.SemiBold,
                FontSize = 13,
                Foreground = foreground,
                Margin = new Thickness(0, 0, 0, 7)
            });
        }
        stack.Children.Add(new TextBlock
        {
            Text = message.Trim(),
            Foreground = secondary,
            TextWrapping = TextWrapping.Wrap
        });

        var card = new Border
        {
            Background = background,
            BorderBrush = borderBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(7),
            Padding = new Thickness(12),
            Width = Math.Max(260d, width),
            MaxWidth = 620d,
            Child = stack,
            SnapsToDevicePixels = true
        };

        var popup = new Popup
        {
            PlacementTarget = target,
            Placement = PlacementMode.Bottom,
            AllowsTransparency = true,
            StaysOpen = true,
            Child = card,
            IsOpen = true
        };

        _activePopup = popup;
        _activeTarget = target;
        _activeCard = card;

        void ScheduleClose(object? _, MouseEventArgs __)
        {
            _closeTimer?.Stop();
            _closeTimer = new DispatcherTimer(DispatcherPriority.Input)
            {
                Interval = TimeSpan.FromMilliseconds(90)
            };
            _closeTimer.Tick += (_, _) =>
            {
                _closeTimer?.Stop();
                if (_activeTarget is null || _activeCard is null ||
                    (!_activeTarget.IsMouseOver && !_activeCard.IsMouseOver))
                    Close();
            };
            _closeTimer.Start();
        }

        _targetLeaveHandler = ScheduleClose;
        _cardLeaveHandler = ScheduleClose;
        _targetUnloadedHandler = (_, _) => Close();
        target.MouseLeave += _targetLeaveHandler;
        card.MouseLeave += _cardLeaveHandler;
        target.Unloaded += _targetUnloadedHandler;
    }

    public static void Close()
    {
        _closeTimer?.Stop();
        _closeTimer = null;

        if (_activeTarget is not null)
        {
            if (_targetLeaveHandler is not null) _activeTarget.MouseLeave -= _targetLeaveHandler;
            if (_targetUnloadedHandler is not null) _activeTarget.Unloaded -= _targetUnloadedHandler;
        }
        if (_activeCard is not null && _cardLeaveHandler is not null)
            _activeCard.MouseLeave -= _cardLeaveHandler;

        if (_activePopup is not null) _activePopup.IsOpen = false;
        _activePopup = null;
        _activeTarget = null;
        _activeCard = null;
        _targetLeaveHandler = null;
        _cardLeaveHandler = null;
        _targetUnloadedHandler = null;
    }
}
