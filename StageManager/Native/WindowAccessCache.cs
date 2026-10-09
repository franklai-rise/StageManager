using StageManager.Native.PInvoke;
using System;
using System.Collections.Generic;
using System.Threading;

namespace StageManager.Native;

/// <summary>
/// Bounded advisory cache, keyed by HWND and its actual owning PID (not a hosted app's PID).
/// No timers, background tasks, or retained native handles. Refresh from a capture worker or
/// another non-render path. Unknown results are cached too, avoiding repeated denied queries.
/// </summary>
public sealed class WindowAccessCache
{
    private const int Capacity = 512;
    private const long LifetimeMilliseconds = 5000;
    private readonly object _gate = new();
    private readonly Dictionary<(IntPtr Window, uint ProcessId), Entry> _entries = new();
    private readonly Func<IntPtr, uint> _owner;
    private readonly Func<IntPtr, WindowAccessInfo> _query;
    private readonly Func<long> _clock;

    public static WindowAccessCache Shared { get; } = new();

    public WindowAccessCache() : this(ReadOwner, WindowAccess.Query, () => Environment.TickCount64) { }

    internal WindowAccessCache(Func<IntPtr, uint> owner, Func<IntPtr, WindowAccessInfo> query, Func<long> clock)
    {
        _owner = owner;
        _query = query;
        _clock = clock;
    }

    /// <summary>
    /// Non-waiting cache read: only a cheap native HWND-owner lookup, never a process/token query.
    /// Miss, expiry, invalid owner, or lock contention returns Unknown. Does not schedule work.
    /// </summary>
    public WindowAccessInfo GetCached(IntPtr window)
    {
        if (window == IntPtr.Zero) return default;
        var processId = _owner(window);
        return processId != 0 && TryRead((window, processId), out var access) ? access : default;
    }

    /// <summary>
    /// Synchronous query-on-miss; run off-render (e.g. inside StartCapture's Task.Run).
    /// Fresh entries, including Unknown, are reused for at most five seconds from query start.
    /// Native work happens outside the cache lock; owner changes discard the result.
    /// </summary>
    public WindowAccessInfo Refresh(IntPtr window)
    {
        if (window == IntPtr.Zero) return default;
        var processId = _owner(window);
        if (processId == 0) return default;
        var key = (window, processId);
        if (TryRead(key, out var cached)) return cached;

        var started = _clock();
        var access = _query(window);
        if (_owner(window) != processId) return default;
        lock (_gate)
        {
            var now = _clock();
            // A slow query cannot extend the freshness window or overwrite a newer completion.
            if (_entries.TryGetValue(key, out var existing) && IsFresh(existing, now))
                return existing.Access;
            if (!IsFresh(new(access, started), now)) return default;

            // Prune expired entries and obsolete owners before enforcing the strict capacity.
            List<(IntPtr Window, uint ProcessId)>? removed = null;
            foreach (var pair in _entries)
            {
                if (!IsFresh(pair.Value, now) || pair.Key.Window == window)
                    (removed ??= new()).Add(pair.Key);
            }
            if (removed is not null)
                foreach (var oldKey in removed) _entries.Remove(oldKey);

            if (_entries.Count >= Capacity)
            {
                var oldestKey = default((IntPtr Window, uint ProcessId));
                var oldestTime = long.MaxValue;
                foreach (var pair in _entries)
                    if (pair.Value.Started < oldestTime)
                    {
                        oldestTime = pair.Value.Started;
                        oldestKey = pair.Key;
                    }
                _entries.Remove(oldestKey);
            }
            _entries[key] = new(access, started);
        }
        return access;
    }

    private bool TryRead((IntPtr Window, uint ProcessId) key, out WindowAccessInfo access)
    {
        access = default;
        if (!Monitor.TryEnter(_gate)) return false;
        try
        {
            if (!_entries.TryGetValue(key, out var entry) || !IsFresh(entry, _clock())) return false;
            access = entry.Access;
            return true;
        }
        finally { Monitor.Exit(_gate); }
    }

    internal int Count { get { lock (_gate) return _entries.Count; } }

    private static bool IsFresh(Entry entry, long now) =>
        now >= entry.Started && now - entry.Started < LifetimeMilliseconds;

    private static uint ReadOwner(IntPtr window) =>
        Win32.GetWindowThreadProcessId(window, out var processId) == 0 ? 0 : processId;

    private readonly record struct Entry(WindowAccessInfo Access, long Started);
}
