namespace Spokes_Server.Core.Services.Core;

using System;
using System.Collections.Generic;

public class StartupTiming
{
    public string Name { get; set; } = "";
    public long Timestamp { get; set; }
}

/// <summary>
/// Circuit-scoped service that tracks startup and first-render milestones across layout,
/// page, and sidebar components, allowing granular diagnostics in startup reports.
/// </summary>
public class StartupTimingService
{
    private readonly List<StartupTiming> _timings = new();
    private readonly object _lock = new();

    public void Add(string name)
    {
        var ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        lock (_lock)
        {
            _timings.Add(new StartupTiming { Name = name, Timestamp = ts });
        }
    }

    public List<StartupTiming> GetTimings()
    {
        lock (_lock)
        {
            return new List<StartupTiming>(_timings);
        }
    }
}
