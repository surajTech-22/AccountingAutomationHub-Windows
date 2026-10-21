using System.IO;
using System.Windows.Media.Imaging;
using AccountingAutomationHub.Domain.Governance;

namespace AccountingAutomationHub.Desktop.Services;

/// <summary>LAN-process Remote Assist visual state shared by the agent and Remote Assist workspace.</summary>
public static class RemoteAssistLocalStreamState
{
    private static readonly object Gate = new();
    private static BitmapImage? _administratorFrame;
    private static long _administratorFrameSequence;
    private static string _sessionId = string.Empty;
    private static bool _shareMyAah = true;
    private static bool _allowAdministratorControl = true;
    private static bool _administratorSharing;
    private static bool _administratorAllowsRemoteControl;
    private static DateTime? _lastRemoteFrameSentUtc;
    private static DateTime? _lastAdministratorFrameUtc;

    public static event EventHandler? Changed;
    public static event EventHandler? FrameChanged;
    public static string SessionId { get { lock (Gate) return _sessionId; } }
    public static bool ShareMyAah { get { lock (Gate) return _shareMyAah; } set { lock (Gate) _shareMyAah = value; Raise(); } }
    public static bool AllowAdministratorControl { get { lock (Gate) return _allowAdministratorControl; } set { lock (Gate) _allowAdministratorControl = value; Raise(); } }
    public static bool AdministratorSharing { get { lock (Gate) return _administratorSharing; } }
    public static bool AdministratorAllowsRemoteControl { get { lock (Gate) return _administratorAllowsRemoteControl; } }
    public static BitmapImage? AdministratorFrame { get { lock (Gate) return _administratorFrame; } }
    public static long AdministratorFrameSequence { get { lock (Gate) return _administratorFrameSequence; } }
    public static DateTime? LastRemoteFrameSentUtc { get { lock (Gate) return _lastRemoteFrameSentUtc; } }
    public static DateTime? LastAdministratorFrameUtc { get { lock (Gate) return _lastAdministratorFrameUtc; } }

    public static void Begin(string sessionId)
    {
        lock (Gate)
        {
            _sessionId = sessionId;
            _shareMyAah = true;
            _allowAdministratorControl = true;
            _administratorFrame = null;
            _administratorFrameSequence = 0;
            _administratorSharing = false;
            _administratorAllowsRemoteControl = false;
            _lastRemoteFrameSentUtc = null;
            _lastAdministratorFrameUtc = null;
        }
        Raise();
    }

    public static void End()
    {
        lock (Gate)
        {
            _sessionId = string.Empty;
            _administratorFrame = null;
            _administratorFrameSequence = 0;
            _administratorSharing = false;
            _administratorAllowsRemoteControl = false;
        }
        Raise();
    }

    public static void MarkRemoteFrameSent(DateTime utc) { lock (Gate) _lastRemoteFrameSentUtc = utc; }

    public static void ApplyServerState(RemoteAssistStreamStateRecord state)
    {
        var changed=false;
        lock (Gate)
        {
            changed = _administratorSharing != state.AdministratorSharing || _administratorAllowsRemoteControl != state.RemoteMayControlAdministrator;
            _administratorSharing = state.AdministratorSharing;
            _administratorAllowsRemoteControl = state.RemoteMayControlAdministrator;
            if(!state.AdministratorSharing && (_administratorFrame is not null || _administratorFrameSequence != 0))
            {
                _administratorFrame = null;
                _administratorFrameSequence = 0;
                _lastAdministratorFrameUtc = null;
                changed = true;
            }
        }
        if(changed)Raise();
    }

    public static void ApplyAdministratorFrame(RemoteAssistFrameRecord frame)
    {
        var image = ToBitmap(frame.JpegBytes);
        lock (Gate)
        {
            _administratorFrame = image;
            _administratorFrameSequence = frame.Sequence;
            _lastAdministratorFrameUtc = frame.CapturedUtc;
        }
        RaiseFrame();
    }

    private static BitmapImage ToBitmap(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes, writable: false);
        var image = new BitmapImage();
        image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad; image.StreamSource = stream; image.EndInit(); image.Freeze();
        return image;
    }

    private static void Raise() => Changed?.Invoke(null, EventArgs.Empty);
    private static void RaiseFrame() => FrameChanged?.Invoke(null, EventArgs.Empty);
}
