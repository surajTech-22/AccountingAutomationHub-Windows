using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace AccountingAutomationHub.Desktop.Behaviors;

/// <summary>
/// Application-wide full-text hover support. A transient ToolTip is opened immediately
/// for text-bearing controls when no explicit view ToolTip exists. This deliberately avoids
/// relying on element-only clipping measurements because virtualized DataGrid cells are often
/// clipped by a parent presenter rather than by the text element itself.
/// </summary>
public static class OverflowToolTipManager
{
    private static bool _enabled;

    /// <summary>
    /// Opt-out switch for pages where automatic full-text hover is distracting. The
    /// property inherits through the visual tree so one setting on a page root disables
    /// only the application-added hover fallback while preserving explicit ToolTips.
    /// </summary>
    public static readonly DependencyProperty SuppressAutoToolTipProperty =
        DependencyProperty.RegisterAttached(
            "SuppressAutoToolTip",
            typeof(bool),
            typeof(OverflowToolTipManager),
            new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.Inherits));

    public static void SetSuppressAutoToolTip(DependencyObject element, bool value) =>
        element.SetValue(SuppressAutoToolTipProperty, value);

    public static bool GetSuppressAutoToolTip(DependencyObject element) =>
        (bool)element.GetValue(SuppressAutoToolTipProperty);

    private static readonly DependencyProperty AutoToolTipProperty =
        DependencyProperty.RegisterAttached(
            "AutoToolTip",
            typeof(ToolTip),
            typeof(OverflowToolTipManager),
            new PropertyMetadata(null));

    public static void Enable()
    {
        if (_enabled) return;
        _enabled = true;

        // TextBlock covers normal display/DataGrid text. ContentControl covers
        // Label/Button/CheckBox/RadioButton/DataGridCell/DataGridColumnHeader and
        // similar WPF controls with text content. ComboBox and headered controls need
        // their own extraction path. Register base classes once to avoid duplicate
        // routed-event handlers on derived controls.
        Register(typeof(TextBlock));
        Register(typeof(TextBox));
        Register(typeof(ContentControl));
        Register(typeof(ComboBox));
        Register(typeof(HeaderedItemsControl));
    }

    private static void Register(Type type)
    {
        EventManager.RegisterClassHandler(type, UIElement.MouseEnterEvent, new MouseEventHandler(OnMouseEnter), true);
        EventManager.RegisterClassHandler(type, UIElement.MouseLeaveEvent, new MouseEventHandler(OnMouseLeave), true);
        EventManager.RegisterClassHandler(type, FrameworkElement.UnloadedEvent, new RoutedEventHandler(OnUnloaded), true);
    }

    private static void OnMouseEnter(object sender, MouseEventArgs e)
    {
        if (sender is not FrameworkElement element || !element.IsVisible) return;

        CloseAutoToolTip(element);
        if (GetSuppressAutoToolTip(element)) return;

        // A view-specific tooltip is authoritative. The global overflow feature only
        // fills the gap where no explicit ToolTip has been assigned.
        if (element.ReadLocalValue(ToolTipService.ToolTipProperty) != DependencyProperty.UnsetValue ||
            ToolTipService.GetToolTip(element) is not null)
            return;

        var text = ExtractText(element).Trim();
        if (text.Length == 0 || !IsOverflowing(element, text)) return;

        var toolTip = new ToolTip
        {
            Content = new TextBlock
            {
                Text = text,
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 640
            },
            PlacementTarget = element,
            Placement = PlacementMode.Mouse,
            StaysOpen = true,
            IsOpen = true
        };
        element.SetValue(AutoToolTipProperty, toolTip);
    }

    private static void OnMouseLeave(object sender, MouseEventArgs e)
    {
        if (sender is FrameworkElement element) CloseAutoToolTip(element);
    }

    private static void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement element) CloseAutoToolTip(element);
    }

    private static void CloseAutoToolTip(FrameworkElement element)
    {
        if (element.GetValue(AutoToolTipProperty) is ToolTip toolTip)
        {
            toolTip.IsOpen = false;
            element.ClearValue(AutoToolTipProperty);
        }
    }

    private static string ExtractText(FrameworkElement element) => element switch
    {
        TextBlock textBlock => textBlock.Text ?? string.Empty,
        TextBox textBox => textBox.Text ?? string.Empty,
        ComboBox comboBox => comboBox.IsEditable && !string.IsNullOrWhiteSpace(comboBox.Text)
            ? comboBox.Text
            : ContentText(comboBox.SelectedItem),
        HeaderedContentControl headeredContent => HeaderText(headeredContent.Header),
        HeaderedItemsControl headeredItems => HeaderText(headeredItems.Header),
        ContentControl contentControl => ContentText(contentControl.Content),
        _ => string.Empty
    };

    private static string HeaderText(object? header) => ContentText(header);

    private static string ContentText(object? content) => content switch
    {
        null => string.Empty,
        string value => value,
        TextBlock textBlock => textBlock.Text ?? string.Empty,
        TextBox textBox => textBox.Text ?? string.Empty,
        ContentControl contentControl => ContentText(contentControl.Content),
        DependencyObject => string.Empty,
        _ => content.ToString() ?? string.Empty
    };

    private static bool IsOverflowing(FrameworkElement element, string text)
    {
        var availableWidth = Math.Max(0, element.ActualWidth);
        var availableHeight = Math.Max(0, element.ActualHeight);
        if (availableWidth <= 1 || availableHeight <= 1) return false;

        FontFamily fontFamily;
        FontStyle fontStyle;
        FontWeight fontWeight;
        FontStretch fontStretch;
        double fontSize;
        Brush foreground;
        TextWrapping wrapping;

        if (element is TextBlock textBlock)
        {
            fontFamily = textBlock.FontFamily;
            fontStyle = textBlock.FontStyle;
            fontWeight = textBlock.FontWeight;
            fontStretch = textBlock.FontStretch;
            fontSize = textBlock.FontSize;
            foreground = textBlock.Foreground;
            wrapping = textBlock.TextWrapping;
            availableWidth -= textBlock.Margin.Left + textBlock.Margin.Right;
        }
        else if (element is Control control)
        {
            fontFamily = control.FontFamily;
            fontStyle = control.FontStyle;
            fontWeight = control.FontWeight;
            fontStretch = control.FontStretch;
            fontSize = control.FontSize;
            foreground = control.Foreground;
            wrapping = element is TextBox box ? box.TextWrapping : TextWrapping.NoWrap;
            availableWidth -= control.Padding.Left + control.Padding.Right + control.BorderThickness.Left + control.BorderThickness.Right;
            availableHeight -= control.Padding.Top + control.Padding.Bottom + control.BorderThickness.Top + control.BorderThickness.Bottom;
        }
        else
        {
            return false;
        }

        availableWidth = Math.Max(1, availableWidth);
        availableHeight = Math.Max(1, availableHeight);

        var formatted = new FormattedText(
            text,
            CultureInfo.CurrentUICulture,
            element.FlowDirection,
            new Typeface(fontFamily, fontStyle, fontWeight, fontStretch),
            fontSize,
            foreground,
            VisualTreeHelper.GetDpi(element).PixelsPerDip);

        if (wrapping == TextWrapping.NoWrap)
        {
            // DesiredSize catches DataGrid/TextBlock layouts where the parent clips the
            // visual before ActualWidth alone exposes the truncation. FormattedText is
            // the fallback for ordinary controls and text boxes.
            return element.DesiredSize.Width > element.ActualWidth + 0.75 ||
                   formatted.WidthIncludingTrailingWhitespace > availableWidth + 0.75;
        }

        formatted.MaxTextWidth = availableWidth;
        return element.DesiredSize.Height > element.ActualHeight + 0.75 ||
               formatted.Height > availableHeight + 0.75;
    }
}
