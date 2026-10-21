using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AccountingAutomationHub.Infrastructure.Persistence;

namespace AccountingAutomationHub.Desktop.Services;

/// <summary>
/// Durable, per-workspace/module cache generations. SQLite is always the accounting
/// authority. This journal is only a crash-recovery watermark for derived snapshots.
/// A committed change is recorded before any replacement cache is allowed to publish.
/// </summary>
public sealed class WorkspaceCacheRevisionStore
{
    private sealed class State
    {
        public readonly object Gate = new();
        public long Revision;
        public long PublishedRevision = -1;
        public bool Dirty = true;
        public string SourceStamp = string.Empty;
    }

    private sealed class Checkpoint
    {
        public Checkpoint() { }
        public string Key { get; set; } = string.Empty;
        public long Revision { get; set; }
        public long PublishedRevision { get; set; } = -1;
        public bool Dirty { get; set; } = true;
        public string SourceStamp { get; set; } = string.Empty;
    }

    private readonly ConcurrentDictionary<string, State> _states = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _folder;
    private readonly Func<string> _sourceStamp;

    public WorkspaceCacheRevisionStore(string folder, Func<string> sourceStamp)
    {
        _folder = folder;
        _sourceStamp = sourceStamp;
        Directory.CreateDirectory(folder);
    }

    private string PathFor(string key) => System.IO.Path.Combine(
        _folder, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key.ToUpperInvariant()))) + ".json");

    private State Get(string key) => _states.GetOrAdd(key, k =>
    {
        var state = new State();
        try
        {
            var checkpoint = JsonSerializer.Deserialize<Checkpoint>(File.ReadAllText(PathFor(k)));
            if (checkpoint is not null && checkpoint.Key.Equals(k, StringComparison.OrdinalIgnoreCase))
            {
                state.Revision = checkpoint.Revision;
                state.PublishedRevision = checkpoint.PublishedRevision;
                state.Dirty = checkpoint.Dirty;
                state.SourceStamp = checkpoint.SourceStamp;
            }
        }
        catch (FileNotFoundException) { }
        catch (DirectoryNotFoundException) { }
        catch (Exception ex)
        {
            PipelineRuntimeDiagnostics.NavigationFailed("Cache checkpoint restore", ex);
            // Missing/corrupt metadata must never make an old cache authoritative.
        }
        return state;
    });

    public long Current(string key)
    {
        var state = Get(key);
        lock (state.Gate) return state.Revision;
    }

    public bool IsCurrent(string key, long revision)
    {
        var state = Get(key);
        lock (state.Gate) return state.Revision == revision;
    }

    public bool IsAuthoritative(string key, long revision)
    {
        var state = Get(key);
        lock (state.Gate)
            return !state.Dirty && state.Revision == revision && state.PublishedRevision == revision;
    }

    public bool CanRestore(string key, long revision)
    {
        var state = Get(key);
        lock (state.Gate)
            return !state.Dirty && state.Revision == revision && state.PublishedRevision == revision &&
                   state.SourceStamp.Equals(_sourceStamp(), StringComparison.Ordinal);
    }

    public string CaptureSourceStamp() => _sourceStamp();

    public long MarkDirty(string key)
    {
        var state = Get(key);
        lock (state.Gate)
        {
            checked { state.Revision++; }
            state.Dirty = true;
            WriteCheckpoint(key, state);
            return state.Revision;
        }
    }

    public bool RunIfCurrent(string key, long revision, Func<bool> action)
    {
        var state = Get(key);
        lock (state.Gate)
            return state.Revision == revision && action();
    }

    public bool CommitIfCurrent(string key, long revision, Action commit) =>
        CommitIfCurrent(key, revision, null, commit);

    public bool CommitIfCurrent(string key, long revision, string? expectedSourceStamp, Action commit)
    {
        var state = Get(key);
        lock (state.Gate)
        {
            if (state.Revision != revision) return false;
            if (expectedSourceStamp is not null &&
                !expectedSourceStamp.Equals(_sourceStamp(), StringComparison.Ordinal))
            {
                state.Dirty = true;
                WriteCheckpoint(key, state);
                return false;
            }
            // File replacement and the generation decision share this lock. A late
            // background writer cannot replace the cache of a newer committed edit.
            commit();
            state.PublishedRevision = revision;
            state.SourceStamp = _sourceStamp();
            state.Dirty = expectedSourceStamp is not null &&
                !expectedSourceStamp.Equals(state.SourceStamp, StringComparison.Ordinal);
            try { WriteCheckpoint(key, state); }
            catch
            {
                state.Dirty = true;
                throw;
            }
            return !state.Dirty;
        }
    }

    public void MarkUncertain(string key)
    {
        var state = Get(key);
        lock (state.Gate)
        {
            checked { state.Revision++; }
            state.Dirty = true;
            WriteCheckpoint(key, state);
        }
    }

    // A failed or interrupted read invalidates its generation, not the last good
    // payload. Restoring that payload remains possible, but it is provisional.
    public static string AccountingSourceStamp() => SourceStamp(ApplicationDataPaths.DatabasePath);
    public static string AuditSourceStamp() => SourceStamp(ApplicationDataPaths.AuditDatabasePath);

    private static string SourceStamp(string databasePath)
    {
        // A checkpoint, external Tally import, network replacement or a write from an
        // older AAH build can change SQLite without passing through this process's hub.
        // The file/WAL watermark makes such a saved projection provisional on next launch.
        var builder = new StringBuilder();
        foreach (var path in new[] { databasePath, databasePath + "-wal" })
        {
            var file = new FileInfo(path);
            builder.Append(file.Exists ? file.Length : -1L).Append(':')
                .Append(file.Exists ? file.LastWriteTimeUtc.Ticks : 0L).Append('|');
        }
        return builder.ToString();
    }

    private void WriteCheckpoint(string key, State state)
    {
        Directory.CreateDirectory(_folder);
        var path = PathFor(key);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, new Checkpoint
                {
                    Key = key,
                    Revision = state.Revision,
                    PublishedRevision = state.PublishedRevision,
                    Dirty = state.Dirty,
                    SourceStamp = state.SourceStamp
                });
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, true);
        }
        finally { try { if (File.Exists(temporary)) File.Delete(temporary); } catch { } }
    }
}
