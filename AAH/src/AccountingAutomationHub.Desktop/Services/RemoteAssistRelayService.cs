using System.Collections.Concurrent;
using AccountingAutomationHub.Domain.Governance;

namespace AccountingAutomationHub.Desktop.Services;

/// <summary>
/// Main-Server in-memory relay for AAH-window-only Remote Assist frames and input events.
/// Frames are intentionally ephemeral and are never written to the accounting database or disk.
/// </summary>
public sealed class RemoteAssistRelayService
{
    private sealed class SessionState
    {
        public readonly object Gate = new();
        public RemoteAssistFrameRecord? RemoteFrame;
        public RemoteAssistFrameRecord? AdministratorFrame;
        public readonly Queue<RemoteAssistInputRecord> RemoteInputs = new();
        public readonly Queue<RemoteAssistInputRecord> AdministratorInputs = new();
        public bool RemoteSharing = true;
        public bool AdministratorMayControlRemote = true;
        public bool AdministratorSharing;
        public bool RemoteMayControlAdministrator;
        public long NextRemoteInputSequence;
        public long NextAdministratorInputSequence;
        public DateTime UpdatedUtc = DateTime.UtcNow;
    }

    private static readonly IReadOnlySet<string> AllowedInputKinds = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "MouseMove", "LeftDown", "LeftUp", "DoubleClick", "RightDown", "RightUp", "Wheel", "KeyDown", "KeyUp", "Text"
    };

    private readonly ConcurrentDictionary<string, SessionState> _sessions = new(StringComparer.OrdinalIgnoreCase);

    private SessionState Get(string sessionId) => _sessions.GetOrAdd(sessionId.Trim(), _ => new SessionState());

    public RemoteAssistStreamStateRecord GetState(string sessionId)
    {
        var state = Get(sessionId);
        lock (state.Gate)
        {
            return new RemoteAssistStreamStateRecord
            {
                SessionId = sessionId,
                RemoteSharing = state.RemoteSharing,
                AdministratorMayControlRemote = state.AdministratorMayControlRemote,
                AdministratorSharing = state.AdministratorSharing,
                RemoteMayControlAdministrator = state.RemoteMayControlAdministrator,
                RemoteFrameSequence = state.RemoteFrame?.Sequence ?? 0,
                AdministratorFrameSequence = state.AdministratorFrame?.Sequence ?? 0,
                UpdatedUtc = state.UpdatedUtc
            };
        }
    }

    public void SetRemoteState(string sessionId, bool sharing, bool administratorMayControl)
    {
        var state = Get(sessionId);
        lock (state.Gate)
        {
            state.RemoteSharing = sharing;
            state.AdministratorMayControlRemote = administratorMayControl;
            state.UpdatedUtc = DateTime.UtcNow;
        }
    }

    public bool SetAdministratorState(string sessionId, bool sharing, bool remoteMayControl)
    {
        var state = Get(sessionId);
        lock (state.Gate)
        {
            var nextControl = sharing && remoteMayControl;
            var changed = state.AdministratorSharing != sharing || state.RemoteMayControlAdministrator != nextControl;
            state.AdministratorSharing = sharing;
            state.RemoteMayControlAdministrator = nextControl;
            state.UpdatedUtc = DateTime.UtcNow;
            return changed;
        }
    }

    public RemoteAssistFrameRecord PublishFrame(string sessionId, string direction, byte[] jpegBytes, int pixelWidth, int pixelHeight)
    {
        if (jpegBytes.Length < 4) throw new InvalidOperationException("Remote Assist frame is empty or invalid.");
        if (jpegBytes.Length > 2_500_000) throw new InvalidOperationException("Remote Assist frame exceeds the 2.5 MB safety limit.");
        if (jpegBytes[0] != 0xFF || jpegBytes[1] != 0xD8 || jpegBytes[^2] != 0xFF || jpegBytes[^1] != 0xD9) throw new InvalidOperationException("Remote Assist accepts JPEG frames only.");
        if (pixelWidth <= 0 || pixelHeight <= 0 || pixelWidth > 4096 || pixelHeight > 4096) throw new InvalidOperationException("Remote Assist frame dimensions are invalid.");
        var state = Get(sessionId);
        lock (state.Gate)
        {
            var previous = direction.Equals(RemoteAssistStreamDirections.AdministratorToRemote, StringComparison.OrdinalIgnoreCase)
                ? state.AdministratorFrame?.Sequence ?? 0
                : state.RemoteFrame?.Sequence ?? 0;
            var frame = new RemoteAssistFrameRecord
            {
                SessionId = sessionId,
                Direction = direction,
                Sequence = previous + 1,
                PixelWidth = pixelWidth,
                PixelHeight = pixelHeight,
                CapturedUtc = DateTime.UtcNow,
                JpegBytes = jpegBytes.ToArray()
            };
            if (direction.Equals(RemoteAssistStreamDirections.AdministratorToRemote, StringComparison.OrdinalIgnoreCase)) state.AdministratorFrame = frame;
            else state.RemoteFrame = frame;
            state.UpdatedUtc = DateTime.UtcNow;
            return frame;
        }
    }

    public RemoteAssistFrameRecord? GetFrame(string sessionId, string direction, long afterSequence = 0)
    {
        var state = Get(sessionId);
        lock (state.Gate)
        {
            var frame = direction.Equals(RemoteAssistStreamDirections.AdministratorToRemote, StringComparison.OrdinalIgnoreCase)
                ? state.AdministratorFrame
                : state.RemoteFrame;
            if (frame is null || frame.Sequence <= afterSequence) return null;
            return new RemoteAssistFrameRecord
            {
                SessionId = frame.SessionId,
                Direction = frame.Direction,
                Sequence = frame.Sequence,
                PixelWidth = frame.PixelWidth,
                PixelHeight = frame.PixelHeight,
                CapturedUtc = frame.CapturedUtc,
                JpegBytes = frame.JpegBytes.ToArray()
            };
        }
    }

    public RemoteAssistInputRecord QueueInput(string sessionId, RemoteAssistInputRecord input)
    {
        ArgumentNullException.ThrowIfNull(input);
        if(!AllowedInputKinds.Contains(input.Kind ?? string.Empty)) throw new InvalidOperationException("Remote Assist input kind is not allow-listed.");
        input.X=Math.Clamp(input.X,0,1);input.Y=Math.Clamp(input.Y,0,1);input.Delta=Math.Clamp(input.Delta,-1200,1200);input.Key=Math.Clamp(input.Key,0,0xFFFF);
        if((input.Text??string.Empty).Length>256)throw new InvalidOperationException("Remote Assist text input exceeds the 256-character safety limit.");
        input.Text=input.Text??string.Empty;
        var state = Get(sessionId);
        lock (state.Gate)
        {
            var toRemote = input.Direction.Equals(RemoteAssistStreamDirections.AdministratorToRemote, StringComparison.OrdinalIgnoreCase);
            input.SessionId = sessionId;
            input.Sequence = toRemote ? ++state.NextRemoteInputSequence : ++state.NextAdministratorInputSequence;
            input.CreatedUtc = DateTime.UtcNow;
            var queue = toRemote ? state.RemoteInputs : state.AdministratorInputs;
            queue.Enqueue(input);
            while (queue.Count > 250) queue.Dequeue();
            state.UpdatedUtc = DateTime.UtcNow;
            return input;
        }
    }

    public IReadOnlyList<RemoteAssistInputRecord> DrainInputs(string sessionId, string direction, long afterSequence)
    {
        var state = Get(sessionId);
        lock (state.Gate)
        {
            var queue = direction.Equals(RemoteAssistStreamDirections.AdministratorToRemote, StringComparison.OrdinalIgnoreCase)
                ? state.RemoteInputs
                : state.AdministratorInputs;
            var rows = queue.Where(x => x.Sequence > afterSequence).Take(100).Select(CloneInput).ToArray();
            if (rows.Length > 0)
            {
                var max = rows[^1].Sequence;
                while (queue.Count > 0 && queue.Peek().Sequence <= max) queue.Dequeue();
            }
            return rows;
        }
    }

    public void RemoveSession(string sessionId) => _sessions.TryRemove(sessionId, out _);

    private static RemoteAssistInputRecord CloneInput(RemoteAssistInputRecord x) => new()
    {
        Sequence = x.Sequence, SessionId = x.SessionId, Direction = x.Direction, Kind = x.Kind,
        X = x.X, Y = x.Y, Delta = x.Delta, Key = x.Key, Text = x.Text,
        Control = x.Control, Shift = x.Shift, Alt = x.Alt, CreatedUtc = x.CreatedUtc
    };
}
