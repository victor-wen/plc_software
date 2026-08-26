namespace PlcSoftware.Core.Services;

/// <summary>
/// Health of the PLC heartbeat signal (D101) as tracked by <see cref="HeartbeatMonitor"/>.
/// </summary>
public enum HeartbeatStatus
{
    /// <summary>No heartbeat has been observed yet.</summary>
    Unknown,

    /// <summary>
    /// The PLC is answering: D101 keeps producing new (different) values within the timeout.
    /// </summary>
    Online,

    /// <summary>
    /// D101 has held the same value for the whole timeout without a change, so the PLC is presumed
    /// to have stopped advancing its heartbeat.
    /// </summary>
    Lost,
}

/// <summary>
/// Tracks the PLC heartbeat counter (D101) and flags when it stops advancing.
///
/// <para><b>Change rule.</b> The monitor does <em>not</em> require D101 to increment by exactly one.
/// Any different value is a change, including the UInt16 wraparound edge (65535 → 0). This keeps the
/// watchdog simple and robust: every new value, whatever it is, proves the PLC is alive.</para>
///
/// <para><b>Timeout rule.</b> When the same D101 value has been observed unchanged for the configured
/// timeout (3 s by default), the monitor transitions to <see cref="HeartbeatStatus.Lost"/> and raises
/// <see cref="StatusChanged"/>. A later change (a different value) transitions it back to
/// <see cref="HeartbeatStatus.Online"/>.</para>
///
/// <para><b>Injectable time.</b> The current time is supplied through a <c>Func&lt;DateTime&gt;</c>
/// (wall-clock time is never read directly), so tests drive the timeout deterministically with a manual
/// clock; callers that want real time simply use the default, which reads <see cref="DateTime.UtcNow"/>.</para>
/// </summary>
public sealed class HeartbeatMonitor
{
    private readonly Func<DateTime> _now;
    private readonly TimeSpan _timeout;
    private ushort? _lastValue;
    private DateTime? _lastChangedAt;

    /// <summary>Builds the monitor. <paramref name="now"/> defaults to <see cref="DateTime.UtcNow"/> and
    /// the timeout to 3 seconds. The timeout must be positive.</summary>
    public HeartbeatMonitor(Func<DateTime>? now = null, TimeSpan? timeout = null)
    {
        _now = now ?? (() => DateTime.UtcNow);
        _timeout = timeout ?? TimeSpan.FromSeconds(3);
        if (_timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout), _timeout, "must be positive.");
        }
    }

    /// <summary>The current heartbeat status. Begins as <see cref="HeartbeatStatus.Unknown"/>.</summary>
    public HeartbeatStatus Status { get; private set; } = HeartbeatStatus.Unknown;

    /// <summary>Raised each time <see cref="Status"/> changes, carrying the new status.</summary>
    public event Action<HeartbeatStatus>? StatusChanged;

    /// <summary>
    /// Feeds one D101 observation. A value different from the previous one is counted as a change
    /// (resuming online and resetting the loss timer); an identical value is counted as "no change"
    /// and, once it has persisted for the timeout, moves the device to
    /// <see cref="HeartbeatStatus.Lost"/>.
    /// </summary>
    public void Observe(ushort value)
    {
        var now = _now();

        if (_lastValue.HasValue && _lastValue.Value == value)
        {
            // No change: if the last change was long enough ago, the heartbeat is presumed lost.
            if (_lastChangedAt is DateTime changedAt && now - changedAt >= _timeout)
            {
                SetStatus(HeartbeatStatus.Lost);
            }

            return;
        }

        // Any different value is a change (no strict +1, wraparound counts). Reset the loss timer.
        _lastValue = value;
        _lastChangedAt = now;
        SetStatus(HeartbeatStatus.Online);
    }

    private void SetStatus(HeartbeatStatus status)
    {
        if (Status == status)
        {
            return;
        }

        Status = status;
        StatusChanged?.Invoke(status);
    }
}
