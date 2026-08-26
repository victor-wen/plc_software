namespace PlcSoftware.Core.Services;

using PlcSoftware.Core.Abstractions;

/// <summary>
/// Immutable outcome of one group cycle: the group that was read, the raw values to be decoded later
/// by an upper layer, and the scheduled (virtual) time offset at which the read was issued.
///
/// <see cref="Registers"/> is populated for register reads (<see cref="PollingArea.HoldingRegisters"/>);
/// <see cref="Bits"/> is populated for bit reads (<see cref="PollingArea.Coils"/> /
/// <see cref="PollingArea.DiscreteInputs"/>); the other is <c>null</c>.
/// </summary>
public sealed record PollingResult(
    PollingGroup Group,
    IReadOnlyList<ushort>? Registers,
    IReadOnlyList<bool>? Bits,
    TimeSpan Timestamp);

/// <summary>
/// Details of one failed group read (a non-cancellation failure), raised through
/// <see cref="PollingService.ReadFailed"/>.
///
/// <see cref="Exception"/> is the transport error that made the read fail; <see cref="Timestamp"/> is the
/// scheduled virtual offset at which the group was due. The connection-health layer observes these
/// failures to enforce the design's 3-consecutive-failure rule (design §5.3); the transport supervisor
/// owns offline detection, not this service.
/// </summary>
public sealed record PollingFailure(
    PollingGroup Group,
    Exception Exception,
    TimeSpan Timestamp);

/// <summary>
/// Executes a <see cref="PollingPlan"/> over a single shared <see cref="IModbusClient"/>, using an
/// injectable <see cref="IAsyncDelay"/> so it never blocks on real wall-clock time (tests drive it
/// deterministically).
///
/// <para><b>Scheduling.</b> A single coordinator loop drives every group. Each group is due at an
/// absolute virtual offset (<c>Timestamp</c>); <see cref="RunAsync"/> runs whichever groups are due at
/// the current instant, then sleeps until the earliest next due, then repeats. Groups fire
/// immediately on start and then every <see cref="PollingGroup.Interval"/> (Fast 250 ms, Process and Io
/// 500 ms in the default plan). The coordinator issues <em>one read at a time</em>, so a group never
/// overlaps itself (no re-entrancy): a slow read simply means the group's next tick waits for it.</para>
///
/// <para><b>Single queue / write fairness.</b> Every read is submitted through <c>_client</c>, which in
/// production is the <c>QueuedModbusClient</c> decorator — a single FIFO, single-flight queue. Polling
/// never re-enqueues: after a cycle it sleeps its interval, yielding the bus, so a write queued by an
/// external caller behind an in-flight read completes before the group's next read (writes are not
/// starved). No special write-pending signal is needed; FIFO + the non-overlap guarantee is the
/// mechanism.</para>
///
/// <para><b>Failures.</b> A read that fails (non-cancellation) is a per-cycle skip — the group is
/// retried on its next interval. Connection health is owned by the transport supervisor, not polling,
/// so a failed read never tears the loop down. Cancellation is observed at every read and delay
/// boundary, so awaiting <see cref="RunAsync"/> joins the loop cleanly.</para>
///
/// <para><b>Time model.</b> <see cref="PollingResult.Timestamp"/> is the scheduled virtual offset from
/// start (the sum of intervals advanced), not wall-clock elapsed time; reads are modelled as taking
/// zero virtual time. This keeps the service deterministic and free of real time in tests.</para>
///
/// <para><b>Real-cadence drift.</b> Because the virtual clock only advances through the delay
/// boundaries, a group's real cadence is its interval <em>plus</em> the time its read actually takes:
/// a read is modelled as zero virtual time, so in production a slow read simply pushes the group's
/// next tick later. There is <em>no catch-up</em> — after a slow cycle the group fires one interval
/// after it finishes, so a sustained read latency drifts the cadence later over time rather than
/// snapping back to a perfect rate. This is deliberate: it preserves the non-overlap guarantee and
/// write fairness. Cadence is not corrected here; the transport supervisor owns connection health.</para>
/// </summary>
public sealed class PollingService
{
    private readonly IReadOnlyList<PollingGroup> _groups;
    private readonly IModbusClient _client;
    private readonly IAsyncDelay _delay;
    private readonly TimeSpan[] _nextDue;

    private int _running;
    private TimeSpan _now;

    /// <summary>Builds the service over <paramref name="plan"/> and the shared single-queue client.</summary>
    public PollingService(PollingPlan plan, IModbusClient client, IAsyncDelay delay)
    {
        if (plan is null)
        {
            throw new ArgumentNullException(nameof(plan));
        }

        _client = client ?? throw new ArgumentNullException(nameof(client));
        _delay = delay ?? throw new ArgumentNullException(nameof(delay));

        _groups = plan.Groups;
        _nextDue = new TimeSpan[_groups.Count];
    }

    /// <summary>Raised after each completed group cycle, carrying that group's raw values.</summary>
    public event Action<PollingResult>? ResultAvailable;

    /// <summary>
    /// Raised after a group read fails with a non-cancellation exception (see <see cref="PollingFailure"/>).
    ///
    /// This is the observability hook for the connection-health layer: it can apply the design §5.3
    /// 3-consecutive-failure rule on top of <see cref="PollingFailure"/>. The transport <em>supervisor</em>
    /// owns offline detection and backoff — a failed read never tears this loop down.
    /// </summary>
    public event EventHandler<PollingFailure>? ReadFailed;

    /// <summary>
    /// Number of consecutive group-read failures since the last successful read (reset on every success).
    /// Lets a connection-health layer observe the design §5.3 3-consecutive-failure rule without
    /// subscribing to <see cref="ReadFailed"/>; offline detection remains the supervisor's job.
    /// </summary>
    public int ConsecutiveReadFailures { get; private set; }

    /// <summary>
    /// Runs the polling plan until <paramref name="cancellationToken"/> is cancelled. Awaiting the
    /// returned task joins the loop; it completes cleanly on cancellation.
    /// </summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0)
        {
            throw new InvalidOperationException("RunAsync is already running for this polling service.");
        }

        try
        {
            _now = TimeSpan.Zero;
            Array.Clear(_nextDue, 0, _nextDue.Length); // all groups fire immediately on start.

            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    var due = FindDueGroups();
                    if (due.Count == 0)
                    {
                        var gap = _nextDue.Min() - _now;
                        await _delay.Delay(gap, cancellationToken);
                        _now += gap;
                        continue;
                    }

                    foreach (var (group, index) in due)
                    {
                        await PollOnceAsync(group, cancellationToken);
                        // Advance even after a failed cycle so the group retries at its interval, not in
                        // a tight retry loop.
                        _nextDue[index] += group.Interval;
                    }
                }
                catch (OperationCanceledException)
                {
                    // Shutdown: whether our own token was cancelled or an in-flight read was cancelled by
                    // a foreign token (e.g. the transport queue shutting down), join the loop cleanly
                    // rather than surfacing the cancellation and faulting the polling loop. Normal
                    // shutdown and a foreign cancellation both end the loop; the loop never faults.
                    break;
                }
            }
        }
        finally
        {
            Interlocked.Exchange(ref _running, 0);
        }
    }

    /// <summary>
    /// Groups due at <see cref="_now"/>, in declaration order, paired with their plan index. Returned
    /// as a list of tuples so the caller can advance the matching <see cref="_nextDue"/> entry.
    /// </summary>
    private List<(PollingGroup Group, int Index)> FindDueGroups()
    {
        var due = new List<(PollingGroup Group, int Index)>();
        for (var i = 0; i < _groups.Count; i++)
        {
            if (_nextDue[i] <= _now)
            {
                due.Add((_groups[i], i));
            }
        }

        return due;
    }

    private async Task PollOnceAsync(PollingGroup group, CancellationToken cancellationToken)
    {
        try
        {
            var result = group.Area switch
            {
                PollingArea.HoldingRegisters => new PollingResult(
                    group,
                    await _client.ReadHoldingRegistersAsync(
                        group.SlaveId, group.StartAddress, group.Count, cancellationToken),
                    null,
                    _now),
                PollingArea.Coils => new PollingResult(
                    group,
                    null,
                    await _client.ReadCoilsAsync(group.SlaveId, group.StartAddress, group.Count, cancellationToken),
                    _now),
                _ => new PollingResult(
                    group,
                    null,
                    await _client.ReadDiscreteInputsAsync(
                        group.SlaveId, group.StartAddress, group.Count, cancellationToken),
                    _now),
            };

            // A successful read resets the consecutive-failure streak used by the connection-health layer.
            ConsecutiveReadFailures = 0;
            ResultAvailable?.Invoke(result);
        }
        catch (OperationCanceledException)
        {
            // Cancellation (shutdown) surfaces to RunAsync, which joins the loop. Don't swallow it.
            throw;
        }
        catch (Exception ex)
        {
            // A failed read is a per-cycle skip; the transport supervisor owns connection health. Expose
            // the failure so the connection-health layer can apply the design §5.3 3-failure rule.
            ConsecutiveReadFailures++;
            ReadFailed?.Invoke(this, new PollingFailure(group, ex, _now));
        }
    }
}
