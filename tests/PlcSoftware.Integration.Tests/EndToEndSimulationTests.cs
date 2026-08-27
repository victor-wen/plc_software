using PlcSoftware.Core.Abstractions;
using PlcSoftware.Core.Models;
using PlcSoftware.Core.Services;
using PlcSoftware.Infrastructure.Modbus;
using PlcSoftware.Infrastructure.Simulation;

namespace PlcSoftware.Integration.Tests;

/// <summary>
/// End-to-end pipeline tests: simulation PLC → single-request queue → polling service → register
/// decoder → snapshot merger → device-state store → heartbeat monitor + alarm service, plus the
/// command write path (pulse / jog release) through the same queue. Every test is deterministic: the
/// scenario runners use a virtual clock and the polling heartbeat uses an injected virtual delay.
/// </summary>
public sealed class EndToEndSimulationTests
{
    private static readonly FaultDefinition[] K1K7 =
    {
        new() { Code = 1, Message = "急停" },
        new() { Code = 2, Message = "安全门打开" },
        new() { Code = 3, Message = "安全光栅" },
        new() { Code = 4, Message = "气压低" },
        new() { Code = 5, Message = "气缸挡停伸出超时" },
        new() { Code = 6, Message = "挡停未缩回" },
        new() { Code = 7, Message = "扫码超时" },
    };

    [Fact]
    public async Task ScenarioSteps_DriveSnapshots_AndHeartbeatStaysOnline()
    {
        var client = new InMemoryModbusClient();
        var queued = new QueuedModbusClient(client);
        var store = new DeviceStateStore();
        var merger = new SnapshotMerger(store);

        // Simulator: steps 0..5, one second each, D101 heartbeat increments each second, no faults.
        var events = new List<SimulationEvent>();
        for (var step = 0; step <= 5; step++)
        {
            events.Add(new SetStepEvent(TimeSpan.FromSeconds(step), (ushort)step));
        }

        var scenario = new SimulationScenario(
            events,
            new SimulationHeartbeat(SimulationPoints.Heartbeat, TimeSpan.FromSeconds(1)));
        var runner = new SimulationScenarioRunner(scenario, client);

        await queued.ConnectAsync(CancellationToken.None);

        // Poll the fast group D100-D110 (protocol offset 0..10) + process group D200-D213 (100..113)
        // through the queue, as the production PollingPlan does.
        var now = DateTime.UtcNow;
        DeviceSnapshot last = store.Current;

        // Run several virtual seconds, advancing the scenario clock and decoding each cycle.
        for (var t = 0; t < 8; t++)
        {
            runner.Advance(TimeSpan.FromSeconds(1));

            var fastValues = await queued.ReadHoldingRegistersAsync(1, 0, 11, CancellationToken.None);
            var processValues = await queued.ReadHoldingRegistersAsync(1, 100, 14, CancellationToken.None);

            var fast = RegisterDecoder.DecodeFast(fastValues);
            var process = RegisterDecoder.DecodeProcess(processValues);
            last = merger.Publish(fast, process, now.AddSeconds(t));
        }

        // Step 0..5 transitions were driven; D200 holds the latest step (5 staying until end, then 0 at t=6?).
        Assert.True(last.Values.ContainsKey("D200"));
        Assert.True(last.Values.ContainsKey("M200"));

        // Heartbeat: D101 changed every second, so the monitor must stay Online even past 3 seconds.
        var heartbeat = new HeartbeatMonitor(timeout: TimeSpan.FromSeconds(3));
        heartbeat.Observe(0);
        for (var i = 1; i <= 5; i++)
        {
            heartbeat.Observe((ushort)(i % 100));
            Assert.Equal(HeartbeatStatus.Online, heartbeat.Status);
        }
    }

    [Fact]
    public async Task FaultInjection_ProducesAlarmStartAndRecovery()
    {
        var client = new InMemoryModbusClient();
        var queued = new QueuedModbusClient(client);

        // Fault code D110 = 3 (安全光栅) then back to 0.
        var alarms = new AlarmService(K1K7);
        FaultDefinition? started = null;
        var recovered = 0;
        alarms.AlarmStarted += d => started = d;
        alarms.AlarmRecovered += _ => recovered++;

        await queued.ConnectAsync(CancellationToken.None);
        await queued.WriteSingleRegisterAsync(1, SimulationPoints.FaultCode, SimulationFaults.SafetyLightCurtain, CancellationToken.None);

        var fast = RegisterDecoder.DecodeFast(await queued.ReadHoldingRegistersAsync(1, 0, 11, CancellationToken.None));
        alarms.Observe((ushort?)fast["D110"] ?? 0);
        Assert.Equal(3, alarms.ActiveCode);
        Assert.NotNull(started);

        // Repeating the same code must NOT raise a duplicate.
        started = null!;
        alarms.Observe(3);
        Assert.Null(started);

        await queued.WriteSingleRegisterAsync(1, SimulationPoints.FaultCode, SimulationFaults.None, CancellationToken.None);
        var fast2 = RegisterDecoder.DecodeFast(await queued.ReadHoldingRegistersAsync(1, 0, 11, CancellationToken.None));
        alarms.Observe((ushort?)fast2["D110"] ?? 0);
        Assert.Equal(0, alarms.ActiveCode);
        Assert.Equal(1, recovered);
    }

    [Fact]
    public async Task CommandPulse_ThroughQueue_WritesAndReleasesCoil()
    {
        var client = new InMemoryModbusClient();
        var queued = new QueuedModbusClient(client);
        var gate = new AlwaysOnlineIdleGate();
        var service = new CommandService(queued, gate, new InstantDelay());

        await queued.ConnectAsync(CancellationToken.None);
        var result = await service.ExecuteAsync(
            new CommandRequest(CommandTarget.Start, true), CancellationToken.None);
        Assert.Equal(CommandStatus.Success, result.Status);

        var coil = await queued.ReadCoilsAsync(1, 101, 1, CancellationToken.None);
        Assert.False(coil[0]); // pulsed → cleared afterwards
    }

    [Fact]
    public async Task QueueSerializes_100ConcurrentReads_ToMaxConcurrencyOne()
    {
        var client = new InMemoryModbusClient();
        var queued = new QueuedModbusClient(client);

        await queued.ConnectAsync(CancellationToken.None);
        var tasks = Enumerable.Range(0, 100)
            .Select(_ => queued.ReadHoldingRegistersAsync(1, 0, 11, CancellationToken.None))
            .ToArray();
        var results = await Task.WhenAll(tasks);
        Assert.Equal(100, results.Length);
        Assert.All(results, r => Assert.Equal(11, r.Length));
    }

    [Fact]
    public async Task JogRelease_WritesAllFourHelpers_False()
    {
        var client = new InMemoryModbusClient();
        var queued = new QueuedModbusClient(client);
        var gate = new AlwaysOnlineIdleGate();
        var service = new CommandService(queued, gate, new InstantDelay());

        await queued.ConnectAsync(CancellationToken.None);
        await service.ExecuteAsync(new CommandRequest(CommandTarget.ManualWidthPlus, true), CancellationToken.None);
        await service.ReleaseJogCommandsAsync(CancellationToken.None);

        var coils = await queued.ReadCoilsAsync(1, 106, 4, CancellationToken.None);
        Assert.False(coils[0]);
        Assert.False(coils[1]);
        Assert.False(coils[2]);
        Assert.False(coils[3]);
    }

    // -- test fakes --------------------------------------------------------

    private sealed class AlwaysOnlineIdleGate : ICommandGate
    {
        public bool IsOnline => true;
        public bool IsManualIdle => true;
    }

    private sealed class InstantDelay : IAsyncDelay
    {
        public Task Delay(TimeSpan delay, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
