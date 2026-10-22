using System;

namespace Spokes_Server.Tests.Helpers;

/// <summary>
/// Controllable TimeProvider for advancing virtual time deterministically in tests.
/// </summary>
public class TestTimeProvider : TimeProvider
{
    private DateTimeOffset _utcNow;
    private long _timestamp;

    public TestTimeProvider(DateTimeOffset? initialTime = null)
    {
        _utcNow = initialTime ?? new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        _timestamp = _utcNow.Ticks;
    }

    public override DateTimeOffset GetUtcNow() => _utcNow;

    public override long GetTimestamp() => _timestamp;

    public void Advance(TimeSpan delta)
    {
        _utcNow = _utcNow.Add(delta);
        _timestamp += delta.Ticks;
    }

    public void SetUtcNow(DateTimeOffset time)
    {
        _utcNow = time;
        _timestamp = time.Ticks;
    }
}
