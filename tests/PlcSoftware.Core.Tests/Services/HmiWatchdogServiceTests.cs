using PlcSoftware.Core.Abstractions;
using PlcSoftware.Core.Services;

namespace PlcSoftware.Core.Tests.Services;

/// <summary>
/// Behavioural tests for <see cref="HmiWatchdogService"/>, the D106 host-watchdog counter
/// (design §5.2: 上位机在线时约每 200 ms 更新 D106).
///
/// The service is exercised over an injectable <see cref="IModbusClient"/> (a recording fake), an
/// injectable <see cref="ICommandGate"/> (whose <see cref="ICommandGate.IsOnline"/> is the 断线
/// writes-forbidden flag, design §5.3) and an injectable <see cref="IAsyncDelay"/> (so the ~200 ms
/// cadence never blocks on real wall clock).
///
/// Verified rules:
///   - the counter advances by one per online tick and wraps around UShort.Max → 0 (UInt16 wraparound);
///   - while 断线 (<see cref="ICommandGate.IsOnline"/> false) the service writes <em>nothing</em> and
///     does <em>not</em> advance the counter;
///   - after reconnect it continues from the current counter value — it neither replays the values that
///     would have been written during the outage nor resets the counter to 0 (design §5.3 重连后只恢复
///    轮询和 D106，不重放未完成的命令);
///   - <see cref="RunAsync"/> paces each write with the ~200 ms cadence.
/// </summary>
public class HmiWatchdogServiceTests
{
    [Fact]
    public async Task Increment_WrapsAround_UInt16()
    {
        var client = new RecordingClient();
        var service = new HmiWatchdogService(client, new FakeGate(), new FakeDelay(), initialValue: ushort.MaxValue);

        // 65535 + 1 wraps to 0 (UInt16 arithmetic), then 0 + 1 = 1.
        await service.AdvanceAsync(CancellationToken.None);
        await service.AdvanceAsync(CancellationToken.None);

        Assert.Equal(new[] { ((ushort)6, (ushort)0), ((ushort)6, (ushort)1) }, client.Writes);
        Assert.Equal((ushort)1, service.CurrentValue);
    }

    [Fact]
    public async Task Offline_DoesNotWrite_AndDoesNotAdvance()
    {
        var client = new RecordingClient();
        var gate = new FakeGate { IsOnline = true };
        var service = new HmiWatchdogService(client, gate, new FakeDelay());

        await service.AdvanceAsync(CancellationToken.None); // online: D106 → 1.

        gate.IsOnline = false; // 断线.
        await service.AdvanceAsync(CancellationToken.None);
        await service.AdvanceAsync(CancellationToken.None);

        // §5.3 forbids writes while 断线; the counter must not advance during the outage either.
        Assert.Equal(new[] { ((ushort)6, (ushort)1) }, client.Writes);
        Assert.Equal((ushort)1, service.CurrentValue);
    }

    [Fact]
    public async Task Reconnect_ContinuesFromCurrentValue_NoReplay_NoReset()
    {
        var client = new RecordingClient();
        var gate = new FakeGate { IsOnline = true };
        var service = new HmiWatchdogService(client, gate, new FakeDelay());

        await service.AdvanceAsync(CancellationToken.None); // online: D106 → 1.

        gate.IsOnline = false; // 断线 for two cadence steps.
        await service.AdvanceAsync(CancellationToken.None);
        await service.AdvanceAsync(CancellationToken.None);

        gate.IsOnline = true; // 重连.
        await service.AdvanceAsync(CancellationToken.None); // continue from current value: D106 → 2.

        // Exactly two D106 writes (1 then 2). Not reset to 0 (would re-write 1), and not a replay of
        // the values that would have been written during the outage (would write 2, 3 … on reconnect).
        Assert.Equal(new[] { ((ushort)6, (ushort)1), ((ushort)6, (ushort)2) }, client.Writes);
        Assert.Equal((ushort)2, service.CurrentValue);
    }

    [Fact]
    public async Task RunAsync_PacesEachWriteWithCadence_UntilCancelled()
    {
        var client = new RecordingClient();
        using var cts = new CancellationTokenSource();
        // The fake cancels the token and throws OCE on its 2nd delay, so exactly two writes happen
        // and the loop exits cleanly.
        var delay = new FakeDelay(cts, cancelOnCall: 2);
        var service = new HmiWatchdogService(client, new FakeGate(), delay);

        await service.RunAsync(cts.Token);

        Assert.Equal(2, client.Writes.Count);
        Assert.Equal(new[] { ((ushort)6, (ushort)1), ((ushort)6, (ushort)2) }, client.Writes);
        // Each write is paced by the ~200 ms cadence.
        Assert.Equal(new[] { 200.0, 200.0 }, delay.Delays.Select(d => d.TotalMilliseconds));
    }

    [Fact]
    public async Task RunAsync_ForeignTokenOperationCanceledDuringWrite_IsTransientSkip_LoopSurvives()
    {
        using var loopCts = new CancellationTokenSource();
        using var foreignCts = new CancellationTokenSource(); // never cancelled — a non-shutdown token.
        // The first D106 write throws an OCE carrying a FOREIGN token. That is not a shutdown signal:
        // the loop must treat it as a transient per-step skip and keep running (D106 keeps ticking).
        var client = new FaultyClient(
            throwOnWrite: 1,
            throwCount: 1,
            toThrow: new OperationCanceledException(foreignCts.Token));
        // The 3rd delay cancels the loop token, so the test terminates after the blip was survived.
        var delay = new FakeDelay(loopCts, cancelOnCall: 3);
        var service = new HmiWatchdogService(client, new FakeGate(), delay);

        await service.RunAsync(loopCts.Token);

        // Steps 2 and 3 still landed (D106 → 2, 3): the foreign-token OCE was a skip, not a shutdown.
        // The counter advances before the write, so the failed step 1 consumed value 1 — the surviving
        // writes begin at 2 (the skipped value was never written to D106).
        Assert.Equal(new[] { ((ushort)6, (ushort)2), ((ushort)6, (ushort)3) }, client.Writes);
        Assert.Equal(new[] { 200.0, 200.0, 200.0 }, delay.Delays.Select(d => d.TotalMilliseconds));
    }

    [Fact]
    public async Task RunAsync_OneOffTransientWriteException_IsSkipped_LoopSurvives()
    {
        using var cts = new CancellationTokenSource();
        // A generic (non-cancellation) transport failure once: per-step skip, not a loop teardown.
        var client = new FaultyClient(
            throwOnWrite: 1,
            throwCount: 1,
            toThrow: new InvalidOperationException("transient transport blip"));
        var delay = new FakeDelay(cts, cancelOnCall: 3);
        var service = new HmiWatchdogService(client, new FakeGate(), delay);

        await service.RunAsync(cts.Token);

        // One-off generic transport failure at step 1: per-step skip, loop survives. Counter advances
        // before the write, so surviving writes begin at 2.
        Assert.Equal(new[] { ((ushort)6, (ushort)2), ((ushort)6, (ushort)3) }, client.Writes);
        Assert.Equal(new[] { 200.0, 200.0, 200.0 }, delay.Delays.Select(d => d.TotalMilliseconds));
    }

    /// <summary>Records every D-register write (address + value). D106 writes target protocol address 6.</summary>
    private sealed class RecordingClient : IModbusClient
    {
        private readonly List<(ushort Address, ushort Value)> _writes = new();

        public IReadOnlyList<(ushort Address, ushort Value)> Writes => _writes.ToArray();

        public Task WriteSingleRegisterAsync(byte slaveId, ushort address, ushort value, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _writes.Add((address, value));
            return Task.CompletedTask;
        }

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

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>
    /// Records every D-register write, but throws a caller-supplied exception on a chosen write call
    /// (a 1-based ordinal range), modelling a one-off transport blip; writes resume afterwards.
    /// </summary>
    private sealed class FaultyClient : IModbusClient
    {
        private readonly int _throwOnWrite;
        private readonly int _throwCount;
        private readonly Exception _toThrow;
        private int _writeCalls;
        private readonly List<(ushort Address, ushort Value)> _writes = new();

        public FaultyClient(int throwOnWrite, int throwCount, Exception toThrow)
        {
            _throwOnWrite = throwOnWrite;
            _throwCount = throwCount;
            _toThrow = toThrow;
        }

        public IReadOnlyList<(ushort Address, ushort Value)> Writes => _writes.ToArray();

        public Task WriteSingleRegisterAsync(byte slaveId, ushort address, ushort value, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _writeCalls++;
            if (_writeCalls >= _throwOnWrite && _writeCalls < _throwOnWrite + _throwCount)
            {
                throw _toThrow;
            }

            _writes.Add((address, value));
            return Task.CompletedTask;
        }

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

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>
    /// Captures each requested delay span. With <paramref name="cancelOnCall"/> set, the Nth call cancels
    /// <paramref name="cts"/> and throws <see cref="OperationCanceledException"/>, simulating shutdown
    /// landing at a cadence boundary. Otherwise it completes immediately (no real wall-clock wait).
    /// </summary>
    private sealed class FakeDelay : IAsyncDelay
    {
        private readonly CancellationTokenSource? _cts;
        private readonly int _cancelOnCall;
        private int _calls;
        private readonly List<TimeSpan> _delays = new();

        public FakeDelay(CancellationTokenSource? cts = null, int cancelOnCall = 0)
        {
            _cts = cts;
            _cancelOnCall = cancelOnCall;
        }

        public IReadOnlyList<TimeSpan> Delays => _delays.ToArray();

        public Task Delay(TimeSpan delay, CancellationToken cancellationToken)
        {
            _delays.Add(delay);
            _calls++;
            if (_cancelOnCall != 0 && _calls == _cancelOnCall)
            {
                _cts?.Cancel();
                throw new OperationCanceledException(_cts?.Token ?? cancellationToken);
            }

            return Task.CompletedTask;
        }
    }

    /// <summary>Controllable link-flag source. <see cref="IsOnline"/> false = 断线 (writes forbidden).</summary>
    private sealed class FakeGate : ICommandGate
    {
        public bool IsOnline { get; set; } = true;
        public bool IsManualIdle { get; set; } = true;
    }
}
