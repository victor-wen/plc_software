using PlcSoftware.Core.Services;

namespace PlcSoftware.Core.Tests.Services;

/// <summary>
/// Behavioural tests for <see cref="HeartbeatMonitor"/> (the D140 heartbeat-lost detector).
///
/// Verified rules:
///   - D140 does NOT have to advance by exactly one: any different value counts as a change and keeps
///     the device online;
///   - UInt16 wraparound (65535 → 0) still counts as a change;
///   - holding the same D140 value for the 3-second timeout without any change moves the device to
///     <see cref="HeartbeatStatus.Lost"/>;
///   - a later change (a different value) resumes <see cref="HeartbeatStatus.Online"/>.
///
/// Time is injected as a <see cref="Func{TResult}"/> clock so no real wall-clock time is used in tests.
/// The constructor rejects a non-positive timeout (mirroring <see cref="ConnectionSupervisor"/>).
/// </summary>
public class HeartbeatMonitorTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(3);

    private readonly ManualClock _clock = new();
    private readonly HeartbeatMonitor _monitor;

    public HeartbeatMonitorTests()
    {
        _monitor = new HeartbeatMonitor(() => _clock.Now);
    }

    [Fact]
    public void Constructor_NonPositiveTimeout_ThrowsArgumentOutOfRange()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new HeartbeatMonitor(() => DateTime.UtcNow, TimeSpan.Zero));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new HeartbeatMonitor(() => DateTime.UtcNow, TimeSpan.FromMilliseconds(-1)));
    }

    [Fact]
    public void FirstObservation_EstablishesBaseline_AndIsOnline()
    {
        _monitor.Observe(100);

        Assert.Equal(HeartbeatStatus.Online, _monitor.Status);
    }

    [Fact]
    public void Observe_AnyDifferentValue_CountsAsChange()
    {
        _monitor.Observe(10);          // baseline.
        _clock.Advance(Timeout);       // if the next value were NOT a change we would be Lost now.
        _monitor.Observe(15);          // a non-+1 change (10 → 15) still resumes online.

        Assert.Equal(HeartbeatStatus.Online, _monitor.Status);
    }

    [Fact]
    public void Observe_UInt16Wraparound_CountsAsChange()
    {
        _monitor.Observe(ushort.MaxValue); // baseline: 65535.
        _clock.Advance(Timeout);
        _monitor.Observe(0);               // wraparound 65535 → 0 still counts as a change.

        Assert.Equal(HeartbeatStatus.Online, _monitor.Status);
    }

    [Fact]
    public void Observe_UnchangedFor3Seconds_EntersLost()
    {
        _monitor.Observe(42);
        _clock.Advance(Timeout);
        _monitor.Observe(42); // identical value, no change across the full timeout.

        Assert.Equal(HeartbeatStatus.Lost, _monitor.Status);
    }

    [Fact]
    public void Observe_UnchangedForLessThan3Seconds_StaysOnline()
    {
        _monitor.Observe(42);
        _clock.Advance(TimeSpan.FromMilliseconds(2999));
        _monitor.Observe(42);
        Assert.Equal(HeartbeatStatus.Online, _monitor.Status);

        _clock.Advance(TimeSpan.FromMilliseconds(1)); // crosses the 3s boundary.
        _monitor.Observe(42);
        Assert.Equal(HeartbeatStatus.Lost, _monitor.Status);
    }

    [Fact]
    public void Observe_ResumedChange_ReturnsOnline()
    {
        _monitor.Observe(42);
        _clock.Advance(Timeout);
        _monitor.Observe(42); // Lost.
        Assert.Equal(HeartbeatStatus.Lost, _monitor.Status);

        _clock.Advance(Timeout);
        _monitor.Observe(43); // a change resumes online.

        Assert.Equal(HeartbeatStatus.Online, _monitor.Status);
    }

    [Fact]
    public void StatusChanged_Event_FiresOnlyOnTransitions()
    {
        var transitions = new List<HeartbeatStatus>();
        _monitor.StatusChanged += transitions.Add;

        _monitor.Observe(11);           // Unknown → Online.
        _monitor.Observe(11);           // same value, no transition.
        _monitor.Observe(11);           // same value, no transition.
        _clock.Advance(Timeout);
        _monitor.Observe(11);           // Online → Lost.
        _clock.Advance(Timeout);
        _monitor.Observe(12);           // Lost → Online.

        Assert.Equal(
            new[] { HeartbeatStatus.Online, HeartbeatStatus.Lost, HeartbeatStatus.Online },
            transitions);
    }

    private sealed class ManualClock
    {
        public DateTime Now { get; private set; } = DateTime.UtcNow;

        public void Advance(TimeSpan by) => Now += by;
    }
}
