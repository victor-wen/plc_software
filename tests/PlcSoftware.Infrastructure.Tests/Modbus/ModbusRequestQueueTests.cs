using PlcSoftware.Core.Abstractions;
using PlcSoftware.Infrastructure.Modbus;

namespace PlcSoftware.Infrastructure.Tests.Modbus;

/// <summary>
/// Behavioural tests for the single-request queue produced by <see cref="ModbusRequestQueue"/> and
/// surfaced by its <see cref="QueuedModbusClient"/> decorator.
///
/// The queue guarantees that at most ONE underlying request is in flight at any instant, in strict
/// FIFO submission order, and provides a single shutdown boundary that cancels the whole backlog.
/// Verified rules (via a probe inner client whose reads block on a controllable gate):
///   - 100 concurrent submissions still yield a maximum observed in-flight depth of 1;
///   - a write queued behind an in-flight read runs after it but before later reads (never cutting
///     a request short);
///   - shutting the queue down cancels the in-flight and every pending request, and rejects further
///     submissions;
///   - an inner failure propagates to that caller only while the queue keeps serving the rest;
///   - cancelling a caller token aborts only that request, leaving the others to proceed;
///   - teardown is bounded: an operation that ignores cancellation cannot hang <c>DisposeAsync</c>
///     forever;
///   - an item whose caller cancels after enqueue but before worker pickup is skipped, never invoking
///     its operation;
///   - lifecycle (<see cref="QueuedModbusClient.ConnectAsync"/> /
///     <see cref="QueuedModbusClient.DisconnectAsync"/>) is serialised with the bus backlog, so a
///     disconnect queued behind an in-flight read waits for it to finish before tearing down the
///     transport.
/// </summary>
public class ModbusRequestQueueTests
{
    [Fact]
    public async Task ConcurrentSubmissions_MaxInFlightDepthIsExactlyOne()
    {
        var probe = new ProbeClient();
        await using var client = new QueuedModbusClient(probe);

        var reads = Enumerable.Range(0, 100)
            .Select(_ => client.ReadCoilsAsync(1, 0, 4, CancellationToken.None))
            .ToArray();

        // The first read is now executing and blocking on the gate; give any (unwanted) parallel
        // reads a chance to pile up, then confirm the queue never held more than one in flight.
        await probe.FirstReadStarted;
        await Task.Delay(50);
        Assert.Equal(1, probe.Active);
        Assert.Equal(1, probe.MaxActive);

        probe.Release();
        await Task.WhenAll(reads);
        Assert.Equal(1, probe.MaxActive);
    }

    [Fact]
    public async Task WriteQueuedBehindRead_RunsAfterReadButBeforeLaterReads()
    {
        var log = new List<string>();
        var probe = new ProbeClient(log);
        await using var client = new QueuedModbusClient(probe);

        var firstRead = client.ReadCoilsAsync(1, 0, 2, CancellationToken.None);
        await probe.FirstReadStarted;                       // in flight, blocked
        var write = client.WriteSingleRegisterAsync(1, 0, 0, CancellationToken.None);
        var secondRead = client.ReadCoilsAsync(1, 0, 2, CancellationToken.None);

        // The write and the later read must not have started while the read is still in flight.
        Assert.Equal(new[] { "ReadCoils" }, log);

        probe.Release();
        await Task.WhenAll(firstRead, write, secondRead);

        Assert.Equal(new[] { "ReadCoils", "WriteSingleRegister", "ReadCoils" }, log);
    }

    [Fact]
    public async Task Shutdown_CancelsInFlightAndPending_AndRejectsNewSubmissions()
    {
        var probe = new ProbeClient();
        var client = new QueuedModbusClient(probe);

        var inFlight = client.ReadCoilsAsync(1, 0, 2, CancellationToken.None);
        await probe.FirstReadStarted;                       // in flight, blocked
        var pending = client.ReadCoilsAsync(1, 0, 2, CancellationToken.None);

        await client.DisposeAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => inFlight);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => client.ReadCoilsAsync(1, 0, 2, CancellationToken.None));
    }

    [Fact]
    public async Task InnerException_PropagatesToThatCaller_QueueKeepsServing()
    {
        var probe = new ProbeClient(throwOnFirst: true);
        await using var client = new QueuedModbusClient(probe);

        var failing = client.ReadCoilsAsync(1, 0, 2, CancellationToken.None);
        var healthy = client.ReadCoilsAsync(1, 0, 2, CancellationToken.None);

        probe.Release();

        await Assert.ThrowsAsync<InvalidOperationException>(() => failing);
        var values = await healthy;
        Assert.Equal(2, values.Length);
    }

    [Fact]
    public async Task CallerCancellation_CancelsOnlyThatRequest_OthersProceed()
    {
        var probe = new ProbeClient();
        await using var client = new QueuedModbusClient(probe);

        var cancelled = new CancellationTokenSource();
        var first = client.ReadCoilsAsync(1, 0, 2, cancelled.Token);
        await probe.FirstReadStarted;                       // in flight, blocked
        var second = client.ReadCoilsAsync(1, 0, 2, CancellationToken.None);

        // Cancel while the first request is still holding the bus: that request alone is aborted.
        cancelled.Cancel();
        probe.Release();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        var values = await second;
        Assert.Equal(2, values.Length);
    }

    [Fact]
    public async Task DisconnectQueuedBehindRead_WaitsForReadToComplete()
    {
        var log = new List<string>();
        var probe = new ProbeClient(log);
        await using var client = new QueuedModbusClient(probe);

        var read = client.ReadCoilsAsync(1, 0, 2, CancellationToken.None);
        await probe.FirstReadStarted;                    // read in flight, blocked on the gate
        var disconnect = client.DisconnectAsync(CancellationToken.None);

        // The disconnect must not start while the read still owns the bus.
        Assert.Equal(new[] { "ReadCoils" }, log);

        probe.Release();
        await Task.WhenAll(read, disconnect);

        Assert.Equal(new[] { "ReadCoils", "Disconnect" }, log);
    }

    [Fact]
    public async Task ConnectQueuedBehindRead_WaitsForReadToComplete()
    {
        var log = new List<string>();
        var probe = new ProbeClient(log);
        await using var client = new QueuedModbusClient(probe);

        var read = client.ReadCoilsAsync(1, 0, 2, CancellationToken.None);
        await probe.FirstReadStarted;                    // read in flight, blocked on the gate
        var connect = client.ConnectAsync(CancellationToken.None);

        // The connect must not run while a read still owns the bus.
        Assert.Equal(new[] { "ReadCoils" }, log);

        probe.Release();
        await Task.WhenAll(read, connect);

        Assert.Equal(new[] { "ReadCoils", "Connect" }, log);
    }

    [Fact]
    public async Task Dispose_WithNeverCompletingOperation_CompletesWithinBudget()
    {
        // A tight shutdown budget so the test proves bounded teardown instead of hanging.
        var queue = new ModbusRequestQueue(TimeSpan.FromMilliseconds(100));
        await using var queueRef = queue;

        var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        // An operation that ignores cancellation and can never complete of its own accord.
        _ = queue.EnqueueAsync<int>(
            _ =>
            {
                started.TrySetResult(true);
                return Task.Delay(-1, CancellationToken.None).ContinueWith(_ => 0);
            },
            CancellationToken.None);

        await started.Task;                              // the op is now in flight, never finishing

        var watch = System.Diagnostics.Stopwatch.StartNew();
        await queue.DisposeAsync();                      // must return, not hang
        watch.Stop();

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), $"Shutdown hung for {watch.Elapsed}.");
        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => queue.EnqueueAsync<object?>(_ => Task.FromResult<object?>(null), CancellationToken.None));
    }

    [Fact]
    public async Task CallerCancelled_AfterEnqueue_BeforePickup_SkipsOperation()
    {
        var queue = new ModbusRequestQueue();
        await using var queueRef = queue;

        var firstStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        // Hold the worker so a second item sits in the channel, not yet picked up.
        var first = queue.EnqueueAsync(
            async token =>
            {
                firstStarted.TrySetResult(true);
                await gate.Task;
            },
            CancellationToken.None);
        await firstStarted.Task;

        var cts = new CancellationTokenSource();
        var invoked = false;
        var second = queue.EnqueueAsync(
            token =>
            {
                invoked = true;
                return Task.CompletedTask;
            },
            cts.Token);

        cts.Cancel();                                    // caller cancels after enqueue, before pickup
        gate.TrySetResult(true);                         // let the worker reach the second item
        await first;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => second);
        Assert.False(invoked);
    }

    /// <summary>
    /// A scripted <see cref="IModbusClient"/> whose reads block on a controllable gate so the tests
    /// can deterministically hold a request in flight. Reads record the name of every executed
    /// operation in submission-execution order and track the in-flight depth of concurrent reads so a
    /// broken (parallel) queue is caught loudly. Optionally throws on the first read to exercise the
    /// exception-isolation rule.
    /// </summary>
    private sealed class ProbeClient : IModbusClient
    {
        private readonly object _sync = new();
        private readonly List<string> _log;
        private readonly bool _block;
        private readonly bool _throwOnFirst;
        private readonly TaskCompletionSource<bool> _gate =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<object> _firstRead =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _reads;

        public ProbeClient(List<string>? log = null, bool throwOnFirst = false)
        {
            _log = log ?? new List<string>();
            _block = true;
            _throwOnFirst = throwOnFirst;
        }

        public int Active { get; private set; }

        public int MaxActive { get; private set; }

        public Task<object> FirstReadStarted => _firstRead.Task;

        public void Release() => _gate.TrySetResult(true);

        public Task<bool[]> ReadCoilsAsync(byte slaveId, ushort address, ushort count, CancellationToken cancellationToken)
            => Read("ReadCoils", cancellationToken, () => new bool[count]);

        public Task<bool[]> ReadDiscreteInputsAsync(byte slaveId, ushort address, ushort count, CancellationToken cancellationToken)
            => Read("ReadDiscreteInputs", cancellationToken, () => new bool[count]);

        public Task<ushort[]> ReadHoldingRegistersAsync(byte slaveId, ushort address, ushort count, CancellationToken cancellationToken)
            => Read("ReadHoldingRegisters", cancellationToken, () => new ushort[count]);

        public Task<ushort[]> ReadInputRegistersAsync(byte slaveId, ushort address, ushort count, CancellationToken cancellationToken)
            => Read("ReadInputRegisters", cancellationToken, () => new ushort[count]);

        public Task WriteSingleCoilAsync(byte slaveId, ushort address, bool value, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Log("WriteSingleCoil");
            return Task.CompletedTask;
        }

        public Task WriteSingleRegisterAsync(byte slaveId, ushort address, ushort value, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Log("WriteSingleRegister");
            return Task.CompletedTask;
        }

        public Task ConnectAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Log("Connect");
            return Task.CompletedTask;
        }

        public Task DisconnectAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Log("Disconnect");
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private async Task<T> Read<T>(string name, CancellationToken cancellationToken, Func<T> produce)
        {
            Log(name);
            int ordinal = Interlocked.Increment(ref _reads);
            if (ordinal == 1)
            {
                _firstRead.TrySetResult(new object());
            }

            Enter();
            try
            {
                if (_block)
                {
                    await _gate.Task.WaitAsync(cancellationToken);
                }

                if (_throwOnFirst && ordinal == 1)
                {
                    throw new InvalidOperationException(name);
                }

                return produce();
            }
            finally
            {
                Exit();
            }
        }

        private void Enter()
        {
            lock (_sync)
            {
                Active++;
                MaxActive = Math.Max(MaxActive, Active);
            }
        }

        private void Exit()
        {
            lock (_sync)
            {
                Active--;
            }
        }

        private void Log(string name)
        {
            lock (_sync)
            {
                _log.Add(name);
            }
        }
    }
}
