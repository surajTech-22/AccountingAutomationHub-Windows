using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AccountingAutomationHub.Domain.Governance;

namespace AccountingAutomationHub.Desktop.Services;

/// <summary>
/// Captures and applies input only to Accounting Automation Hub's own WPF main window.
/// No desktop-wide capture or arbitrary process/window targeting is exposed.
/// </summary>
public static class RemoteAssistWindowService
{
    private const int WmMouseMove = 0x0200;
    private const int WmLButtonDown = 0x0201;
    private const int WmLButtonUp = 0x0202;
    private const int WmLButtonDblClk = 0x0203;
    private const int WmRButtonDown = 0x0204;
    private const int WmRButtonUp = 0x0205;
    private const int WmMouseWheel = 0x020A;
    private const int WmKeyDown = 0x0100;
    private const int WmKeyUp = 0x0101;
    private const int WmChar = 0x0102;
    private const int MkLButton = 0x0001;
    private const int MkRButton = 0x0002;
    private const int VkControl = 0x11;
    private const int VkShift = 0x10;
    private const int VkMenu = 0x12;

    public static async Task<RemoteAssistFrameRecord?> CaptureMainWindowAsync(string sessionId, string direction)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null) return null;
        return await dispatcher.InvokeAsync(() => CaptureMainWindow(sessionId, direction)).Task.ConfigureAwait(false);
    }

    private static RemoteAssistFrameRecord? CaptureMainWindow(string sessionId, string direction)
    {
        if (System.Windows.Application.Current?.MainWindow is not Window window || !window.IsLoaded) return null;
        // Capture the exact WPF client visual that receives Remote Assist input.  Using Window.ActualWidth
        // while later targeting the native HWND client rectangle can drift under WindowChrome/DPI scaling.
        // The content root gives capture and input one shared coordinate space.
        var captureRoot = GetCaptureRoot(window);
        var sourceWidth = Math.Max(1.0, captureRoot.ActualWidth);
        var sourceHeight = Math.Max(1.0, captureRoot.ActualHeight);
        if (sourceWidth < 40 || sourceHeight < 40) return null;

        const double maxWidth = 1440.0;
        const double maxHeight = 900.0;
        var scale = Math.Min(1.0, Math.Min(maxWidth / sourceWidth, maxHeight / sourceHeight));
        var width = Math.Max(1, (int)Math.Round(sourceWidth * scale));
        var height = Math.Max(1, (int)Math.Round(sourceHeight * scale));

        var brush = new VisualBrush(captureRoot) { Stretch = Stretch.Fill, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top };
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen()) dc.DrawRectangle(brush, null, new Rect(0, 0, width, height));
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder = new JpegBitmapEncoder { QualityLevel = 62 };
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return new RemoteAssistFrameRecord
        {
            SessionId = sessionId,
            Direction = direction,
            PixelWidth = width,
            PixelHeight = height,
            CapturedUtc = DateTime.UtcNow,
            JpegBytes = stream.ToArray()
        };
    }

    public static async Task ApplyInputToMainWindowAsync(RemoteAssistInputRecord input)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null) return;
        await dispatcher.InvokeAsync(() => ApplyInput(input)).Task.ConfigureAwait(false);
    }

    private static void ApplyInput(RemoteAssistInputRecord input)
    {
        if (System.Windows.Application.Current?.MainWindow is not Window window || !window.IsLoaded) return;
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero) return;
        _ = GetClientRect(handle, out var rect);
        var clientWidth = Math.Max(1, rect.Right - rect.Left);
        var clientHeight = Math.Max(1, rect.Bottom - rect.Top);

        // Map the normalized point back through the exact WPF visual used for capture, then convert
        // screen pixels to this HWND's client pixels.  This removes grid/window-position assumptions
        // and stays correct across DPI, WindowChrome resize borders, window moves and different scales.
        var captureRoot = GetCaptureRoot(window);
        var local = new Point(
            Math.Clamp(input.X, 0, 1) * Math.Max(1.0, captureRoot.ActualWidth - 1.0),
            Math.Clamp(input.Y, 0, 1) * Math.Max(1.0, captureRoot.ActualHeight - 1.0));
        NativePoint screenPoint;
        NativePoint clientPoint;
        try
        {
            var screen = captureRoot.PointToScreen(local);
            screenPoint = new NativePoint { X = (int)Math.Round(screen.X), Y = (int)Math.Round(screen.Y) };
            clientPoint = screenPoint;
            if (!ScreenToClient(handle, ref clientPoint)) throw new InvalidOperationException("Could not convert Remote Assist point to the AAH client surface.");
        }
        catch
        {
            // Conservative compatibility fallback for a transient presentation-source failure.
            clientPoint = new NativePoint
            {
                X = (int)Math.Round(Math.Clamp(input.X, 0, 1) * (clientWidth - 1)),
                Y = (int)Math.Round(Math.Clamp(input.Y, 0, 1) * (clientHeight - 1))
            };
            screenPoint = clientPoint;
            _ = ClientToScreen(handle, ref screenPoint);
        }
        var x = Math.Clamp(clientPoint.X, 0, clientWidth - 1);
        var y = Math.Clamp(clientPoint.Y, 0, clientHeight - 1);
        var point = MakeLParam(x, y);
        _ = SetForegroundWindow(handle);

        switch (input.Kind)
        {
            case "MouseMove":
                PostMessage(handle, WmMouseMove, IntPtr.Zero, point);
                break;
            case "LeftDown":
                PostMessage(handle, WmLButtonDown, new IntPtr(MkLButton), point);
                break;
            case "LeftUp":
                PostMessage(handle, WmLButtonUp, IntPtr.Zero, point);
                break;
            case "DoubleClick":
                PostMessage(handle, WmLButtonDblClk, new IntPtr(MkLButton), point);
                break;
            case "RightDown":
                PostMessage(handle, WmRButtonDown, new IntPtr(MkRButton), point);
                break;
            case "RightUp":
                PostMessage(handle, WmRButtonUp, IntPtr.Zero, point);
                break;
            case "Wheel":
                PostMessage(handle, WmMouseWheel, new IntPtr(input.Delta << 16), MakeLParam(screenPoint.X, screenPoint.Y));
                break;
            case "KeyDown":
                PostModifierDown(handle, input);
                PostMessage(handle, WmKeyDown, new IntPtr(input.Key), IntPtr.Zero);
                break;
            case "KeyUp":
                PostMessage(handle, WmKeyUp, new IntPtr(input.Key), IntPtr.Zero);
                PostModifierUp(handle, input);
                break;
            case "Text":
                foreach (var ch in input.Text ?? string.Empty) PostMessage(handle, WmChar, new IntPtr(ch), IntPtr.Zero);
                break;
        }
    }


    private static void PostModifierDown(IntPtr handle, RemoteAssistInputRecord input)
    {
        if (input.Control) PostMessage(handle, WmKeyDown, new IntPtr(VkControl), IntPtr.Zero);
        if (input.Shift) PostMessage(handle, WmKeyDown, new IntPtr(VkShift), IntPtr.Zero);
        if (input.Alt) PostMessage(handle, WmKeyDown, new IntPtr(VkMenu), IntPtr.Zero);
    }

    private static void PostModifierUp(IntPtr handle, RemoteAssistInputRecord input)
    {
        if (input.Alt) PostMessage(handle, WmKeyUp, new IntPtr(VkMenu), IntPtr.Zero);
        if (input.Shift) PostMessage(handle, WmKeyUp, new IntPtr(VkShift), IntPtr.Zero);
        if (input.Control) PostMessage(handle, WmKeyUp, new IntPtr(VkControl), IntPtr.Zero);
    }

    private static FrameworkElement GetCaptureRoot(Window window)
        => window.Content as FrameworkElement ?? window;

    private static IntPtr MakeLParam(int x, int y) => new((y << 16) | (x & 0xFFFF));

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool PostMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool GetClientRect(IntPtr hWnd, out NativeRect rect);

    [DllImport("user32.dll")]
    private static extern bool ClientToScreen(IntPtr hWnd, ref NativePoint point);

    [DllImport("user32.dll")]
    private static extern bool ScreenToClient(IntPtr hWnd, ref NativePoint point);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left; public int Top; public int Right; public int Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint { public int X; public int Y; }
}
