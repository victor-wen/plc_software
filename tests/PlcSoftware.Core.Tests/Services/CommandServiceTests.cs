using PlcSoftware.Core.Abstractions;
using PlcSoftware.Core.Models;
using PlcSoftware.Core.Services;

namespace PlcSoftware.Core.Tests.Services;

/// <summary>
/// Behavioural tests for <see cref="CommandService"/> (the host command surface of design §4.4).
///
/// The service is exercised over an injectable <see cref="IModbusClient"/> (a recording fake), an
/// injectable <see cref="ICommandGate"/> (the link/mode state source) and an injectable
/// <see cref="IAsyncDelay"/> (so the ~200 ms pulse never blocks on real wall-clock time).
///
/// Verified rules:
///   - a pulse command writes the coil true, waits ~200 ms, then writes it false (order and timing
///     asserted through a shared event log and the fake delay's recorded spans);
///   - <see cref="ICommandService.ReleaseJogCommandsAsync"/> writes M106-M109 all false ("切页/窗口失焦
///     复位"), and does so best-effort (one write per coil, independent of the gate);
///   - a jog is rejected with <see cref="CommandStatus.Rejected"/> when the link is offline or the
///     machine is not manual-idle (design §5.2/§6.4: 断线 and 非手动运行状态 both deny manual output);
///   - a write that times out mid-pulse returns <see cref="CommandStatus.Unknown"/> and does <em>not</em>
///     repeat the pulse or schedule the release write (design §5.3: 结果未知，不盲目重复启动或复位).
/// </summary>
public class CommandServiceTests
{
    [Fact]
    public async Task Pulse_SetCoilTrue_Wait200ms_SetCoilFalse_InOrder()
    {
        var log = new List<string>();
        var client = new RecordingClient(log);
        var delay = new FakeDelay(log);
        var service = new CommandService(client, new FakeGate(), delay);

        var result = await service.ExecuteAsync(new CommandRequest(CommandTarget.EStopRequest), CancellationToken.None);

        Assert.Equal(CommandStatus.Success, result.Status);
        // One pulse: true, ~200 ms, false — in that exact order.
        Assert.Equal(new[] { "W:100:True", "D:200", "W:100:False" }, log);
        Assert.Equal(new[] { (100, true), (100, false) }, client.Writes);
        // The fake delay was asked to wait exactly the ~200 ms pulse width.
        Assert.Equal(new[] { 200.0 }, delay.Delays.Select(d => d.TotalMilliseconds));
    }

    [Fact]
    public async Task ReleaseJogCommands_WritesAllJogCoilsFalse()
    {
        var log = new List<string>();
        var client = new RecordingClient(log);
        var service = new CommandService(client, new FakeGate(), new FakeDelay(log));

        await service.ReleaseJogCommandsAsync(CancellationToken.None);

        // M106-M109 (手动调宽+/-, 皮带点动, 挡停) are all released false on window blur / page switch.
        Assert.Equal(
            new[] { (106, false), (107, false), (108, false), (109, false) },
            client.Writes);
    }

    [Theory]
    [InlineData(false, true)]  // link offline → reject.
    [InlineData(true, false)]  // not manual-idle → reject.
    public async Task Jog_Rejected_WhenOffline_OrNotManualIdle(bool isOnline, bool isManualIdle)
    {
        var log = new List<string>();
        var client = new RecordingClient(log);
        var gate = new FakeGate { IsOnline = isOnline, IsManualIdle = isManualIdle };
        var service = new CommandService(client, gate, new FakeDelay(log));

        var result = await service.ExecuteAsync(new CommandRequest(CommandTarget.ManualWidthPlus), CancellationToken.None);

        Assert.Equal(CommandStatus.Rejected, result.Status);
        Assert.Empty(client.Writes); // no coil write was attempted.
    }

    [Fact]
    public async Task Pulse_WriteTimeout_DoesNotRepeatPulse()
    {
        var log = new List<string>();
        // The very first write (the set-true edge) times out.
        var client = new RecordingClient(log, failOnWriteNumber: 1);
        var delay = new FakeDelay(log);
        var service = new CommandService(client, new FakeGate(), delay);

        var result = await service.ExecuteAsync(new CommandRequest(CommandTarget.Start), CancellationToken.None);

        Assert.Equal(CommandStatus.Unknown, result.Status);
        Assert.Single(client.Writes);       // exactly one attempt: no repe-pulse, no release write.
        Assert.Empty(delay.Delays);         // the pulse aborted before the delay boundary.
        Assert.Equal(new[] { (101, true) }, client.Writes);
    }

    [Fact]
    public async Task Jog_OnlineManualIdle_WritesCoilTrue()
    {
        var log = new List<string>();
        var client = new RecordingClient(log);
        var service = new CommandService(client, new FakeGate(), new FakeDelay(log));

        var result = await service.ExecuteAsync(new CommandRequest(CommandTarget.ManualBeltJog), CancellationToken.None);

        Assert.Equal(CommandStatus.Success, result.Status);
        Assert.Equal(new[] { (108, true) }, client.Writes);
    }

    [Fact]
    public async Task Holding_WritesTheRequestedValue()
    {
        var log = new List<string>();
        var client = new RecordingClient(log);
        var service = new CommandService(client, new FakeGate(), new FakeDelay(log));

        var result = await service.ExecuteAsync(new CommandRequest(CommandTarget.AutoMode, Value: true), CancellationToken.None);

        Assert.Equal(CommandStatus.Success, result.Status);
        Assert.Equal(new[] { (104, true) }, client.Writes);
    }

    /// <summary>Records every coil write (address + value) and, optionally, times out the Nth write.</summary>
    private sealed class RecordingClient : IModbusClient
    {
        private readonly List<string> _log;
        private readonly int _failOnWriteNumber;
        private int _writeCount;
        private readonly List<(int Address, bool Value)> _writes = new();

        public RecordingClient(List<string> log, int failOnWriteNumber = 0)
        {
            _log = log;
            _failOnWriteNumber = failOnWriteNumber;
        }

        public IReadOnlyList<(int Address, bool Value)> Writes => _writes.ToArray();

        public Task WriteSingleCoilAsync(byte slaveId, ushort address, bool value, CancellationToken cancellationToken)
        {
            var ordinal = ++_writeCount;
            _writes.Add((address, value));
            _log.Add($"W:{address}:{(value ? "True" : "False")}");

            if (_failOnWriteNumber != 0 && ordinal == _failOnWriteNumber)
            {
                throw new TimeoutException("simulated write response timeout");
            }

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

        public Task WriteSingleRegisterAsync(byte slaveId, ushort address, ushort value, CancellationToken cancellationToken)
            => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>Records each requested delay span; completes immediately (no real wall-clock wait).</summary>
    private sealed class FakeDelay : IAsyncDelay
    {
        private readonly List<string> _log;
        private readonly List<TimeSpan> _delays = new();

        public FakeDelay(List<string> log)
        {
            _log = log;
        }

        public IReadOnlyList<TimeSpan> Delays => _delays.ToArray();

        public Task Delay(TimeSpan delay, CancellationToken cancellationToken)
        {
            _delays.Add(delay);
            _log.Add($"D:{delay.TotalMilliseconds}");
            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// Controllable link/mode-flag source. <see cref="IsOnline"/> false = 断线 (offline, writes forbidden);
    /// <see cref="IsManualIdle"/> false = 非手动运行状态 (the machine is not manual-idle).
    /// </summary>
    private sealed class FakeGate : ICommandGate
    {
        public bool IsOnline { get; set; } = true;
        public bool IsManualIdle { get; set; } = true;
    }
}
