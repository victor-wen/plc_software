using PlcSoftware.Core.Abstractions;
using PlcSoftware.Core.Services;

namespace PlcSoftware.Core.Tests.Services;

/// <summary>
/// Behavioural tests for the polling plan (<see cref="PollingPlan"/>) and the service that executes
/// it (<see cref="PollingService"/>).
///
/// The plan models the three design groups and the service schedules them over the shared
/// <see cref="IModbusClient"/> using an injectable <see cref="IAsyncDelay"/> (no real wall-clock time in
/// tests, so virtual time is advanced deterministically by releasing the fake delay).
///
/// Verified rules:
///   - each group fires at its own interval (fast 250 ms / process 500 ms / io X / io Y 500 ms) under
///     virtual time, and cancellation joins the loop and stops every group. The I/O diagnostic group
///     reads both the X-input and Y-coil areas (two separate Modbus requests);
///   - a slow request never causes re-entrancy: the next tick of a group waits until the previous
///     execution of that group completes (no overlapping reads of one group);
///   - a write submitted by an external caller through the same client is not starved: writing into
///     the shared FIFO/single-flight bus behind an in-flight read completes before the group's next
///     read (polling yields its scheduling slot between group cycles);
///   - a failed (non-cancellation) read is a per-cycle skip observed through <see cref="PollingFailure"/>
///     / <see cref="PollingService.ReadFailed"/>: the loop survives, the failure counter advances and
///     the next tick still fires at the group's interval (no tight retry spin);
///   - an <see cref="OperationCanceledException"/> — whether from the service's own token or a foreign
///     token cancelling an in-flight read — joins the loop cleanly without faulting <c>RunAsync</c>.
/// </summary>
public class PollingServiceTests
{
    private static readonly TimeSpan FastInterval = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan ProcessInterval = TimeSpan.FromMilliseconds(500);

    private static async Task WaitFor(Func<bool> condition, string message, int timeoutMs = 5000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.ElapsedMilliseconds > timeoutMs)
            {
                throw new TimeoutException(message);
            }

            await Task.Delay(10);
        }
    }

    /// <summary>
    /// The default plan encodes the design groups at their nominal intervals: the fast group every
    /// 250 ms, the process and both I/O area groups (X inputs and Y coils) every 500 ms. Under virtual
    /// time (instant reads) the service fires them at those absolute offsets, and cancelling the token
    /// stops all of them.
    /// </summary>
    [Fact]
    public async Task Groups_FireAtTheirFrequency_UnderVirtualTime_AndCancellationStopsAll()
    {
        var client = new ScriptedClient();
        var manual = new ManualDelay();
        var service = new PollingService(PollingPlan.Default(), client, manual);
        var results = CollectResults(service);

        using var cts = new CancellationTokenSource();
        var run = service.RunAsync(cts.Token);
        try
        {
            // t = 0: fast, process, Io (X inputs) and Io.Y (Y coils) all fire immediately.
            await WaitFor(() => results.Count == 4, "all groups did not fire at t=0.");
            AssertCount(results, "Fast", 1);
            AssertCount(results, "Process", 1);
            AssertCount(results, "Io", 1);
            AssertCount(results, "Io.Y", 1);
            Assert.All(results, r => Assert.Equal(TimeSpan.Zero, r.Timestamp));

            // t = 250: only the fast group fires.
            manual.ReleaseOne();
            await WaitFor(() => results.Count == 5, "fast group did not fire at t=250.");
            AssertCount(results, "Fast", 2);
            AssertCount(results, "Process", 1);
            AssertCount(results, "Io", 1);
            AssertCount(results, "Io.Y", 1);

            // t = 500: fast, process and both I/O area groups all fire.
            manual.ReleaseOne();
            await WaitFor(() => results.Count == 9, "groups did not fire at t=500.");
            AssertCount(results, "Fast", 3);
            AssertCount(results, "Process", 2);
            AssertCount(results, "Io", 2);
            AssertCount(results, "Io.Y", 2);

            // Absolute offsets: Fast at 0/250/500, Process, Io and Io.Y at 0/500.
            AssertOffsets(results, "Fast", TimeSpan.Zero, FastInterval, TimeSpan.FromMilliseconds(500));
            AssertOffsets(results, "Process", TimeSpan.Zero, ProcessInterval);
            AssertOffsets(results, "Io", TimeSpan.Zero, ProcessInterval);
            AssertOffsets(results, "Io.Y", TimeSpan.Zero, ProcessInterval);

            // Every scheduling gap is the fast group's 250 ms (it drives the schedule under virtual time).
            Assert.NotEmpty(manual.Requests);
            Assert.All(manual.Requests, d => Assert.Equal(FastInterval, d));
        }
        finally
        {
            cts.Cancel();
            await run; // joins the loop; cancellation stops all groups without leaking a task.
        }

        Assert.True(run.IsCompleted);
    }

    /// <summary>
    /// A slow read of a group must not let that group overlap itself: while the first read of the fast
    /// group is still in flight, the group does not start a second read, and only completes one per
    /// cycle. The next tick waits until the previous execution completes.
    /// </summary>
    [Fact]
    public async Task SlowRead_DoesNotCauseReentrancy_NextTickWaitsForCompletion()
    {
        var client = new BlockingClient(blockFirstRead: true);
        var manual = new ManualDelay();
        var service = new PollingService(Plan(FastInterval), client, manual);

        using var cts = new CancellationTokenSource();
        var run = service.RunAsync(cts.Token);
        try
        {
            // First read of the fast group starts and blocks on the gate.
            await client.FirstReadStarted;
            Assert.Equal(1, client.ReadsStarted);

            // While still blocked, no second read may be started (no re-entrancy of one group).
            await Task.Delay(30);
            Assert.Equal(1, client.ReadsStarted);

            // Complete the slow read: the group then waits its interval before the next tick.
            client.ReleaseFirstRead();
            await WaitFor(() => client.ReadsStarted == 1 && manual.Requests.Count >= 1,
                "fast group did not wait its interval after the slow read.");
            Assert.Equal(1, client.ReadsStarted); // still no second read before the interval elapses
            Assert.Equal(FastInterval, manual.Requests[^1]);

            // After the interval elapses the next tick runs — but never overlapping the previous one.
            manual.ReleaseOne();
            await WaitFor(() => client.ReadsStarted == 2, "fast group did not run its second tick.");
            Assert.Equal(2, client.ReadsStarted);
            Assert.Equal(1, client.MaxActive);
        }
        finally
        {
            cts.Cancel();
            await run;
        }
    }

    /// <summary>
    /// A write submitted by an external caller through the same client is not starved. The chosen
    /// mechanism is FIFO + single-flight (production <c>QueuedModbusClient</c>) combined with the
    /// service's non-overlap guarantee: a write queued behind an in-flight read runs once that read
    /// completes, before the group's next read — because polling yields its next scheduling slot
    /// (sleeps its interval) between group cycles instead of re-enqueueing immediately.
    /// </summary>
    [Fact]
    public async Task QueuedWrite_CompletesBeforeNextGroupRead_WriteNotStarved()
    {
        var client = new BlockingClient(blockFirstRead: true);
        var manual = new ManualDelay();
        var service = new PollingService(Plan(FastInterval), client, manual);

        using var cts = new CancellationTokenSource();
        var run = service.RunAsync(cts.Token);
        try
        {
            // First read is in flight and holds the single bus.
            await client.FirstReadStarted;

            // An external write arrives through the same client: it is queued behind the in-flight read.
            var write = client.WriteSingleRegisterAsync(1, 0, 0x1234, CancellationToken.None);
            Assert.False(write.IsCompleted, "write must be queued behind the in-flight read.");

            // Let the in-flight read complete: only then does the queued write run.
            client.ReleaseFirstRead();
            await write;                              // the queued write completes before the next read
            Assert.Contains("Write", client.Log);     // it did run

            // The service yields its scheduling slot (sleeps its interval) before the next read, so the
            // queued write completes well before the group's next read.
            await WaitFor(() => manual.Requests.Count >= 1, "service did not schedule the next tick.");
            manual.ReleaseOne();
            await WaitFor(() => client.ReadsStarted == 2, "group's next read did not run.");

            Assert.Equal(new[] { "Read:11", "Write", "Read:11" }, client.Log);
        }
        finally
        {
            cts.Cancel();
            await run;
        }
    }

    /// <summary>
    /// The plan and service validate their inputs up front so a bad configuration surfaces immediately
    /// rather than as a broken runtime loop.
    /// </summary>
    [Fact]
    public void Constructor_RejectsNullDependencies()
    {
        var plan = Plan(FastInterval);
        var client = new ScriptedClient();
        var manual = new ManualDelay();

        Assert.Throws<ArgumentNullException>(() => new PollingService(null!, client, manual));
        Assert.Throws<ArgumentNullException>(() => new PollingService(plan, null!, manual));
        Assert.Throws<ArgumentNullException>(() => new PollingService(plan, client, null!));
        Assert.Throws<ArgumentNullException>(() => new PollingPlan(null!));
    }

    [Fact]
    public void Constructor_RejectsNonPositiveIntervalOrCount()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new PollingGroup("Fast", TimeSpan.Zero, 1, 0, 11));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PollingGroup("Fast", FastInterval, 1, 0, 0));
    }

    [Fact]
    public void Constructor_RejectsEmptyPlan()
    {
        Assert.Throws<ArgumentException>(() => new PollingPlan(Array.Empty<PollingGroup>()));
    }

    /// <summary>
    /// Design §5.1 requires the I/O diagnostic group to read the X/Y regions per the point map, so the
    /// default plan must carry two area entries: the X inputs (FC02) and the Y coils (FC01) — each with
    /// the offset ranges from <c>config/point-map.simulation.json</c>.
    /// </summary>
    [Fact]
    public void DefaultPlan_IoGroup_CoversBothXInputsAndYCoils()
    {
        var groups = PollingPlan.Default().Groups;

        var ioX = groups.Single(g => g.Name == "Io");
        Assert.Equal(PollingArea.DiscreteInputs, ioX.Area);
        Assert.Equal(0, ioX.StartAddress);   // X0 appears in the point map at protocol address 0.
        Assert.Equal(19, ioX.Count);         // X0-X22 → protocol addresses 0-18.

        var ioY = groups.Single(g => g.Name == "Io.Y");
        Assert.Equal(PollingArea.Coils, ioY.Area);
        Assert.Equal(0, ioY.StartAddress);   // Y0 appears in the point map at protocol address 0.
        Assert.Equal(14, ioY.Count);         // Y0-Y15 → protocol addresses 0-13.
    }

    /// <summary>
    /// A failed (non-cancellation) read is a per-cycle skip, never a loop fault. The failure is observed
    /// through <see cref="PollingService.ReadFailed"/> / <see cref="PollingService.ConsecutiveReadFailures"/>
    /// so the connection-health layer can apply §5.3's 3-consecutive-failure rule (the supervisor owns
    /// offline detection). The loop survives, the next tick still fires at the group's interval, and the
    /// service never busy-retries in a tight spin.
    /// </summary>
    [Fact]
    public async Task FailedRead_DoesNotFaultLoop_NextTickAtInterval_NoTightSpin()
    {
        var client = new FailingClient();
        var manual = new ManualDelay();
        var service = new PollingService(Plan(FastInterval), client, manual);
        var failures = new List<PollingFailure>();
        service.ReadFailed += (_, f) =>
        {
            lock (failures)
            {
                failures.Add(f);
            }
        };

        using var cts = new CancellationTokenSource();
        var run = service.RunAsync(cts.Token);
        try
        {
            // t = 0: the fast group is due, its read fails (non-cancellation). The loop survives and the
            // failure is observed, carrying the group and the scheduled (virtual) timestamp.
            await WaitFor(() => client.AttemptedReads == 1, "first read did not run.");
            await WaitFor(() => failures.Count == 1, "read failure was not observed.");
            Assert.Equal(1, service.ConsecutiveReadFailures);
            Assert.Equal("Fast", LastFailure(failures).Group.Name);
            Assert.Equal(TimeSpan.Zero, LastFailure(failures).Timestamp);

            // No tight spin: after a failed cycle the service sleeps its full interval before retrying.
            await WaitFor(() => manual.Requests.Count == 1, "service did not schedule its interval after the failure.");
            Assert.Equal(FastInterval, manual.Requests[^1]);

            // The next tick still fires at the interval; the failure is observed again and the loop lives.
            manual.ReleaseOne();
            await WaitFor(() => client.AttemptedReads == 2, "fast group's next tick did not run.");
            await WaitFor(() => failures.Count == 2, "second read failure was not observed.");
            Assert.Equal(2, service.ConsecutiveReadFailures);
            Assert.Equal(FastInterval, manual.Requests[^1]);
        }
        finally
        {
            cts.Cancel();
            await run;
        }

        Assert.True(run.IsCompleted);
        Assert.False(run.IsFaulted); // a read failure never tears the loop down.
    }

    /// <summary>
    /// The consecutive-failure counter resets on the next successful read, so the connection-health layer
    /// observes a rolling streak rather than an absolute total.
    /// </summary>
    [Fact]
    public async Task SuccessfulRead_ResetsConsecutiveFailureCounter()
    {
        var client = new FailingClient(failFirstOnly: true);
        var manual = new ManualDelay();
        var service = new PollingService(Plan(FastInterval), client, manual);

        using var cts = new CancellationTokenSource();
        var run = service.RunAsync(cts.Token);
        try
        {
            await WaitFor(() => service.ConsecutiveReadFailures == 1, "first read did not fail.");
            manual.ReleaseOne(); // the second tick succeeds.
            await WaitFor(() => service.ConsecutiveReadFailures == 0, "counter did not reset after a success.");
            Assert.Equal(2, client.AttemptedReads); // one failed + one successful read.
        }
        finally
        {
            cts.Cancel();
            await run;
        }
    }

    /// <summary>
    /// An <see cref="OperationCanceledException"/> raised with a foreign token (e.g. the transport queue
    /// cancelling an in-flight read on shutdown) must join the loop cleanly rather than escaping
    /// <see cref="PollingService.RunAsync"/> and faulting the polling loop.
    /// </summary>
    [Fact]
    public async Task ForeignTokenCancellation_JoinsLoopCleanly_DoesNotFault()
    {
        var client = new ForeignOceClient();
        var manual = new ManualDelay();
        var service = new PollingService(Plan(FastInterval), client, manual);

        using var cts = new CancellationTokenSource();
        var run = service.RunAsync(cts.Token);

        // The read is cancelled with a foreign token, not the service's own token — the service must treat
        // it as a loop end and join cleanly instead of surfacing the cancellation as a fault.
        await run;

        Assert.True(run.IsCompleted);
        Assert.False(run.IsFaulted);
        Assert.False(run.IsCanceled);
    }

    private static PollingPlan Plan(TimeSpan interval)
        => new(new[] { new PollingGroup("Fast", interval, 1, 0, 11) });

    private static IReadOnlyList<PollingResult> CollectResults(PollingService service)
    {
        var results = new List<PollingResult>();
        service.ResultAvailable += r =>
        {
            lock (results)
            {
                results.Add(r);
            }
        };
        return results;
    }

    private static void AssertCount(IReadOnlyList<PollingResult> results, string group, int expected)
    {
        lock (results)
        {
            Assert.Equal(expected, results.Count(r => r.Group.Name == group));
        }
    }

    private static void AssertOffsets(IReadOnlyList<PollingResult> results, string group, params TimeSpan[] expected)
    {
        lock (results)
        {
            var timestamps = results
                .Where(r => r.Group.Name == group)
                .Select(r => r.Timestamp)
                .OrderBy(t => t)
                .ToArray();
            Assert.Equal(expected, timestamps);
        }
    }

    private static PollingFailure LastFailure(List<PollingFailure> failures)
    {
        lock (failures)
        {
            return failures[^1];
        }
    }

    private sealed class ManualDelay : IAsyncDelay
    {
        private readonly object _sync = new();
        private readonly List<TimeSpan> _requests = new();
        private readonly Queue<TaskCompletionSource<bool>> _waiters = new();

        public IReadOnlyList<TimeSpan> Requests
        {
            get
            {
                lock (_sync)
                {
                    return _requests.ToArray();
                }
            }
        }

        public Task Delay(TimeSpan delay, CancellationToken cancellationToken)
        {
            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_sync)
            {
                _requests.Add(delay);
                _waiters.Enqueue(tcs);
            }

            if (cancellationToken.CanBeCanceled)
            {
                cancellationToken.Register(() => tcs.TrySetCanceled(cancellationToken));
            }

            return tcs.Task;
        }

        public void ReleaseOne()
        {
            TaskCompletionSource<bool> tcs;
            lock (_sync)
            {
                if (_waiters.Count == 0)
                {
                    throw new InvalidOperationException("no pending delay to release.");
                }

                tcs = _waiters.Dequeue();
            }

            tcs.TrySetResult(true);
        }
    }

    /// <summary>
    /// A non-blocking <see cref="IModbusClient"/> that returns zeroed arrays of the requested type and
    /// length. Used to exercise the plan/schedule without any transport behaviour.
    /// </summary>
    private sealed class ScriptedClient : IModbusClient
    {
        public Task ConnectAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task DisconnectAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<bool[]> ReadCoilsAsync(byte slaveId, ushort address, ushort count, CancellationToken cancellationToken)
            => Task.FromResult(new bool[count]);

        public Task<bool[]> ReadDiscreteInputsAsync(byte slaveId, ushort address, ushort count, CancellationToken cancellationToken)
            => Task.FromResult(new bool[count]);

        public Task<ushort[]> ReadHoldingRegistersAsync(byte slaveId, ushort address, ushort count, CancellationToken cancellationToken)
            => Task.FromResult(new ushort[count]);

        public Task<ushort[]> ReadInputRegistersAsync(byte slaveId, ushort address, ushort count, CancellationToken cancellationToken)
            => Task.FromResult(new ushort[count]);

        public Task WriteSingleCoilAsync(byte slaveId, ushort address, bool value, CancellationToken cancellationToken)
            => Task.CompletedTask;

        public Task WriteSingleRegisterAsync(byte slaveId, ushort address, ushort value, CancellationToken cancellationToken)
            => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>
    /// A client that serialises every bus operation through a single FIFO slot (mirroring the production
    /// <c>QueuedModbusClient</c>) and, optionally, makes the first read block on a controllable gate so the
    /// tests can hold a request in flight. It records the execution order and how many reads were
    /// <em>started</em> so the schedule's non-overlap guarantee can be asserted.
    /// </summary>
    private sealed class BlockingClient : IModbusClient
    {
        private readonly SemaphoreSlim _bus = new(1, 1);
        private readonly object _lock = new();
        private readonly List<string> _log = new();
        private readonly bool _blockFirstRead;
        private readonly TaskCompletionSource<bool> _gate =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> _firstReadStarted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _readsStarted;
        private int _active;
        private int _maxActive;

        public BlockingClient(bool blockFirstRead)
        {
            _blockFirstRead = blockFirstRead;
        }

        public Task FirstReadStarted => _firstReadStarted.Task;

        public int ReadsStarted => Volatile.Read(ref _readsStarted);

        public int MaxActive => Volatile.Read(ref _maxActive);

        public IReadOnlyList<string> Log
        {
            get
            {
                lock (_lock)
                {
                    return _log.ToArray();
                }
            }
        }

        public void ReleaseFirstRead() => _gate.TrySetResult(true);

        public async Task<ushort[]> ReadHoldingRegistersAsync(byte slaveId, ushort address, ushort count, CancellationToken cancellationToken)
        {
            var ordinal = Interlocked.Increment(ref _readsStarted);
            await _bus.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                WriteLog($"Read:{count}");
                var active = Interlocked.Increment(ref _active);
                UpdateMax(active);
                try
                {
                    if (_blockFirstRead && ordinal == 1)
                    {
                        _firstReadStarted.TrySetResult(true);
                        await _gate.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
                    }

                    return new ushort[count];
                }
                finally
                {
                    Interlocked.Decrement(ref _active);
                }
            }
            finally
            {
                _bus.Release();
            }
        }

        public async Task WriteSingleRegisterAsync(byte slaveId, ushort address, ushort value, CancellationToken cancellationToken)
        {
            await _bus.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                WriteLog("Write");
            }
            finally
            {
                _bus.Release();
            }
        }

        public Task ConnectAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task DisconnectAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<bool[]> ReadCoilsAsync(byte slaveId, ushort address, ushort count, CancellationToken cancellationToken)
            => Task.FromResult(new bool[count]);

        public Task<bool[]> ReadDiscreteInputsAsync(byte slaveId, ushort address, ushort count, CancellationToken cancellationToken)
            => Task.FromResult(new bool[count]);

        public Task<ushort[]> ReadInputRegistersAsync(byte slaveId, ushort address, ushort count, CancellationToken cancellationToken)
            => Task.FromResult(new ushort[count]);

        public Task WriteSingleCoilAsync(byte slaveId, ushort address, bool value, CancellationToken cancellationToken)
            => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private void WriteLog(string entry)
        {
            lock (_lock)
            {
                _log.Add(entry);
            }
        }

        private void UpdateMax(int active)
        {
            lock (_lock)
            {
                _maxActive = Math.Max(_maxActive, active);
            }
        }
    }

    /// <summary>
    /// A client whose reads fail with a non-cancellation exception, optionally only on the first read.
    /// Used to prove that a failed read is a per-cycle skip (the loop survives, the next tick fires at
    /// the interval, and the failure is observed) and that the consecutive-failure counter resets on
    /// the next success.
    /// </summary>
    private sealed class FailingClient : IModbusClient
    {
        private readonly bool _failFirstOnly;
        private int _attempts;

        public FailingClient(bool failFirstOnly = false)
        {
            _failFirstOnly = failFirstOnly;
        }

        public int AttemptedReads => Volatile.Read(ref _attempts);

        private void Fail()
        {
            var attempt = Interlocked.Increment(ref _attempts);
            if (!_failFirstOnly || attempt == 1)
            {
                throw new IOException("transport down");
            }
        }

        public Task ConnectAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task DisconnectAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<bool[]> ReadCoilsAsync(byte slaveId, ushort address, ushort count, CancellationToken cancellationToken)
        {
            Fail();
            return Task.FromResult(new bool[count]);
        }

        public Task<bool[]> ReadDiscreteInputsAsync(byte slaveId, ushort address, ushort count, CancellationToken cancellationToken)
        {
            Fail();
            return Task.FromResult(new bool[count]);
        }

        public Task<ushort[]> ReadHoldingRegistersAsync(byte slaveId, ushort address, ushort count, CancellationToken cancellationToken)
        {
            Fail();
            return Task.FromResult(new ushort[count]);
        }

        public Task<ushort[]> ReadInputRegistersAsync(byte slaveId, ushort address, ushort count, CancellationToken cancellationToken)
        {
            Fail();
            return Task.FromResult(new ushort[count]);
        }

        public Task WriteSingleCoilAsync(byte slaveId, ushort address, bool value, CancellationToken cancellationToken)
            => Task.CompletedTask;

        public Task WriteSingleRegisterAsync(byte slaveId, ushort address, ushort value, CancellationToken cancellationToken)
            => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>
    /// A client whose reads are cancelled with a <em>foreign</em> token (distinct from the token the
    /// <see cref="PollingService"/> is running under), e.g. the transport queue cancelling an in-flight
    /// request on shutdown. Used to prove that such a cancellation joins the loop cleanly instead of
    /// faulting <c>RunAsync</c>.
    /// </summary>
    private sealed class ForeignOceClient : IModbusClient
    {
        private readonly CancellationTokenSource _foreign = new();

        public ForeignOceClient()
        {
            _foreign.Cancel();
        }

        private Task<T> Fail<T>()
            => throw new OperationCanceledException("foreign token cancelled the in-flight read", _foreign.Token);

        public Task ConnectAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task DisconnectAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<bool[]> ReadCoilsAsync(byte slaveId, ushort address, ushort count, CancellationToken cancellationToken)
            => Fail<bool[]>();

        public Task<bool[]> ReadDiscreteInputsAsync(byte slaveId, ushort address, ushort count, CancellationToken cancellationToken)
            => Fail<bool[]>();

        public Task<ushort[]> ReadHoldingRegistersAsync(byte slaveId, ushort address, ushort count, CancellationToken cancellationToken)
            => Fail<ushort[]>();

        public Task<ushort[]> ReadInputRegistersAsync(byte slaveId, ushort address, ushort count, CancellationToken cancellationToken)
            => Fail<ushort[]>();

        public Task WriteSingleCoilAsync(byte slaveId, ushort address, bool value, CancellationToken cancellationToken)
            => Task.CompletedTask;

        public Task WriteSingleRegisterAsync(byte slaveId, ushort address, ushort value, CancellationToken cancellationToken)
            => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
