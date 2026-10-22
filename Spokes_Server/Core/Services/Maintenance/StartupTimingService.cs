namespace Spokes_Server.Core.Services.Maintenance;

// DIAG-TEMP: single switch for the temporary chat transition diagnostics (server marks + client report).
public static class ChatDiagnostics
{
    public static readonly bool Enabled = true;
}

public class StartupTiming
{
    public string Name { get; set; } = string.Empty;
    public long Timestamp { get; set; }
    // DIAG-TEMP: sub-millisecond Unix time used by chat transition reports
    public double TimestampMs { get; set; }
}

// DIAG-TEMP: incremental pull result for the client chat transition report.
public sealed class ChatTransitionDrainResult
{
    public int TransitionId { get; init; }
    public string TransitionName { get; init; } = string.Empty;
    public List<StartupTiming> Entries { get; init; } = [];
    public int NextCursor { get; init; }
    public int OpenSpans { get; init; }
    public double LastEntryAgeMs { get; init; }
    public double ServerNowMs { get; init; }
}

/// <summary>
/// Circuit-scoped service that tracks startup and first-render milestones across layout,
/// page, and sidebar components, allowing granular diagnostics in startup reports.
/// </summary>
public class StartupTimingService
{
    private readonly List<StartupTiming> _timings = [];
    private readonly object _lock = new();

    // DIAG-TEMP: chat transition ring buffer (entries tagged by transition id, pulled by the client)
    private sealed class Transition
    {
        public int Id { get; init; }
        public string Name { get; set; } = string.Empty;
        public bool IsNamed { get; set; }
        public double StartedMs { get; init; }
        public double LastEntryMs { get; set; }
        public int OpenSpans { get; set; }
        public List<StartupTiming> Entries { get; } = [];
    }

    private readonly List<Transition> _transitions = [];
    private int _nextTransitionId;
    private const int MaxTransitions = 8;
    private const double TransitionWindowMs = 20000;
    private const double EnsureReuseWindowMs = 2000;

    public static double NowMs() => (DateTime.UtcNow - DateTime.UnixEpoch).TotalMilliseconds;

    /// <summary>DIAG-TEMP: live Chat page instances in this circuit (detects remount overlap).</summary>
    public int ChatLiveInstances;

    public void Add(string name)
    {
        var ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        lock (_lock)
        {
            _timings.Add(new StartupTiming { Name = name, Timestamp = ts });
        }
        Mark(name);
    }

    public List<StartupTiming> GetTimings()
    {
        lock (_lock)
        {
            return [.. _timings];
        }
    }

    // ─── DIAG-TEMP: chat transition diagnostics ─────────────────────────────

    /// <summary>Id of the transition currently accepting marks, or 0 when none.</summary>
    public int CurrentTransitionId
    {
        get
        {
            if (!ChatDiagnostics.Enabled) return 0;
            var now = NowMs();
            lock (_lock)
            {
                return GetCurrentLocked(now)?.Id ?? 0;
            }
        }
    }

    /// <summary>Starts a new transition (client-initiated transitions start unnamed and are named by the first <see cref="EnsureTransition"/>).</summary>
    public int BeginTransition(string name, bool isNamed = true)
    {
        if (!ChatDiagnostics.Enabled) return 0;
        var now = NowMs();
        lock (_lock)
        {
            var t = new Transition { Id = ++_nextTransitionId, Name = name, IsNamed = isNamed, StartedMs = now, LastEntryMs = now };
            t.Entries.Add(new StartupTiming { Name = $"Server: Transition #{t.Id} begun ({name})", Timestamp = (long)now, TimestampMs = now });
            _transitions.Add(t);
            if (_transitions.Count > MaxTransitions)
            {
                _transitions.RemoveAt(0);
            }
            return t.Id;
        }
    }

    /// <summary>
    /// Reuses the current transition when it began less than 2s ago (e.g. the client already began it on tap),
    /// otherwise starts a new server-side transition. Records <paramref name="stepName"/> either way.
    /// </summary>
    public int EnsureTransition(string name, string stepName)
    {
        if (!ChatDiagnostics.Enabled) return 0;
        var now = NowMs();
        int id;
        lock (_lock)
        {
            var current = GetCurrentLocked(now);
            if (current != null && now - current.StartedMs < EnsureReuseWindowMs)
            {
                if (!current.IsNamed)
                {
                    current.Name = name;
                    current.IsNamed = true;
                }
                id = current.Id;
            }
            else
            {
                id = 0;
            }
        }
        if (id == 0)
        {
            id = BeginTransition(name);
        }
        Mark(stepName, id);
        return id;
    }

    public void Mark(string name, int? transitionId = null)
    {
        if (!ChatDiagnostics.Enabled) return;
        var now = NowMs();
        lock (_lock)
        {
            var t = transitionId.HasValue ? FindLocked(transitionId.Value) : GetCurrentLocked(now);
            if (t == null || now - t.StartedMs > TransitionWindowMs) return;
            t.Entries.Add(new StartupTiming { Name = name, Timestamp = (long)now, TimestampMs = now });
            t.LastEntryMs = now;
        }
    }

    /// <summary>Backwards-compatible alias used by existing call sites.</summary>
    public void AddChatTransition(string name) => Mark(name);

    /// <summary>Marks "X Start" now and "X End (+N ms)" on dispose; open spans keep the client report from closing early.</summary>
    public IDisposable Span(string name, int? transitionId = null)
    {
        if (!ChatDiagnostics.Enabled) return NoopSpan.Instance;
        var id = transitionId ?? CurrentTransitionId;
        if (id == 0) return NoopSpan.Instance;
        lock (_lock)
        {
            var t = FindLocked(id);
            if (t == null) return NoopSpan.Instance;
            t.OpenSpans++;
        }
        Mark($"{name} Start", id);
        return new TransitionSpan(this, name, id, NowMs());
    }

    public ChatTransitionDrainResult Drain(int transitionId, int cursor)
    {
        var now = NowMs();
        lock (_lock)
        {
            var t = FindLocked(transitionId);
            if (t == null)
            {
                return new ChatTransitionDrainResult { TransitionId = transitionId, NextCursor = cursor, ServerNowMs = now };
            }
            var start = Math.Clamp(cursor, 0, t.Entries.Count);
            return new ChatTransitionDrainResult
            {
                TransitionId = t.Id,
                TransitionName = t.Name,
                Entries = t.Entries.GetRange(start, t.Entries.Count - start),
                NextCursor = t.Entries.Count,
                OpenSpans = t.OpenSpans,
                LastEntryAgeMs = now - t.LastEntryMs,
                ServerNowMs = now
            };
        }
    }

    private void EndSpan(string name, int id, double startedMs)
    {
        lock (_lock)
        {
            var t = FindLocked(id);
            if (t != null && t.OpenSpans > 0) t.OpenSpans--;
        }
        Mark($"{name} End (+{NowMs() - startedMs:F0}ms)", id);
    }

    private Transition? GetCurrentLocked(double now)
    {
        if (_transitions.Count == 0) return null;
        var last = _transitions[^1];
        return now - last.StartedMs <= TransitionWindowMs ? last : null;
    }

    private Transition? FindLocked(int id)
    {
        for (int i = _transitions.Count - 1; i >= 0; i--)
        {
            if (_transitions[i].Id == id) return _transitions[i];
        }
        return null;
    }

    private sealed class NoopSpan : IDisposable
    {
        public static readonly NoopSpan Instance = new();
        public void Dispose() { }
    }

    private sealed class TransitionSpan(StartupTimingService owner, string name, int id, double startedMs) : IDisposable
    {
        private int _disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                owner.EndSpan(name, id, startedMs);
            }
        }
    }
}
