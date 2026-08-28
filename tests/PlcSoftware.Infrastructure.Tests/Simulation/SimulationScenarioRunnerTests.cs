using PlcSoftware.Infrastructure.Simulation;

namespace PlcSoftware.Infrastructure.Tests.Simulation;

/// <summary>
/// Deterministic scenario-runner tests. The runner drives a <see cref="SimulationMemory"/> over a
/// manually-advanced virtual clock (<see cref="ISimulationClock"/>) — no real wall clock, no async
/// delay. Every assertion is a pure function of how much virtual time was advanced, so all coverage
/// (steps 0-5, K1-K7 faults, heartbeat, delays, timeouts, disconnect/recovery) replays byte-for-byte.
/// </summary>
public class SimulationScenarioRunnerTests
{
    private static async Task<ushort> ReadHoldingRegisterAsync(InMemoryModbusClient client, ushort address)
        => (await client.ReadHoldingRegistersAsync(1, address, 1, CancellationToken.None))[0];

    private static async Task AssertStepAsync(InMemoryModbusClient client, ushort expected)
    {
        Assert.Equal(expected, await ReadHoldingRegisterAsync(client, SimulationPoints.StepRegister));
        // D102 is the packed register mirroring M200-M215 (bit i = M(200+i); M200 = bit0). The
        // scenario emits the current step as the single set bit.
        Assert.Equal(
            (ushort)(1 << expected),
            await ReadHoldingRegisterAsync(client, SimulationPoints.StepBitsRegister));
        for (ushort i = 0; i < SimulationPoints.StepFlagCount; i++)
        {
            var coil = (await client.ReadCoilsAsync(
                1, (ushort)(SimulationPoints.FirstStepFlag + i), 1, CancellationToken.None))[0];
            Assert.Equal(i == expected, coil);
        }
    }

    // --- Deterministic time advance: steps 0 -> 5 -----------------------------------

    [Fact]
    public async Task Advance_ThroughScheduledDelays_DrivesStepsZeroToFive()
    {
        var client = new InMemoryModbusClient();
        await client.ConnectAsync(CancellationToken.None);
        var scenario = new SimulationScenario(new SimulationEvent[]
        {
            new SetStepEvent(TimeSpan.Zero, 0),
            new SetStepEvent(TimeSpan.FromSeconds(1), 1),
            new SetStepEvent(TimeSpan.FromSeconds(2), 2),
            new SetStepEvent(TimeSpan.FromSeconds(3), 3),
            new SetStepEvent(TimeSpan.FromSeconds(4), 4),
            new SetStepEvent(TimeSpan.FromSeconds(5), 5),
        });
        var runner = new SimulationScenarioRunner(scenario, client);

        runner.Advance(TimeSpan.Zero);
        await AssertStepAsync(client, 0);

        // The step-1 delay has not elapsed, so the step must not advance.
        runner.Advance(TimeSpan.FromMilliseconds(999));
        await AssertStepAsync(client, 0);

        // Crossing the delay boundary advances exactly one step.
        runner.Advance(TimeSpan.FromMilliseconds(1));
        await AssertStepAsync(client, 1);

        runner.Advance(TimeSpan.FromSeconds(1));
        await AssertStepAsync(client, 2);

        runner.Advance(TimeSpan.FromSeconds(1));
        await AssertStepAsync(client, 3);

        runner.Advance(TimeSpan.FromSeconds(1));
        await AssertStepAsync(client, 4);

        runner.Advance(TimeSpan.FromSeconds(1));
        await AssertStepAsync(client, 5);
    }

    [Fact]
    public async Task Advance_AppliesEventsInChronologicalOrder_RegardlessOfRegistrationOrder()
    {
        var client = new InMemoryModbusClient();
        await client.ConnectAsync(CancellationToken.None);
        var scenario = new SimulationScenario(new SimulationEvent[]
        {
            new SetStepEvent(TimeSpan.FromSeconds(2), 2),
            new SetStepEvent(TimeSpan.FromMilliseconds(100), 1),
            new SetStepEvent(TimeSpan.Zero, 0),
        });
        var runner = new SimulationScenarioRunner(scenario, client);

        runner.Advance(TimeSpan.FromMilliseconds(100));
        await AssertStepAsync(client, 1);

        runner.Advance(TimeSpan.FromMilliseconds(1900));
        await AssertStepAsync(client, 2);
    }

    [Fact]
    public async Task Advance_LongJump_AppliesAllIntermediateEventsDeterministically()
    {
        var client = new InMemoryModbusClient();
        await client.ConnectAsync(CancellationToken.None);
        var scenario = new SimulationScenario(new SimulationEvent[]
        {
            new SetStepEvent(TimeSpan.FromMilliseconds(100), 1),
            new SetStepEvent(TimeSpan.FromMilliseconds(200), 2),
            new SetStepEvent(TimeSpan.FromMilliseconds(300), 3),
        });
        var runner = new SimulationScenarioRunner(scenario, client);

        runner.Advance(TimeSpan.FromMilliseconds(300));
        await AssertStepAsync(client, 3);
    }

    // --- Heartbeat (D101) ------------------------------------------------------------

    [Fact]
    public async Task Advance_ElapsedWholePeriods_IncrementHeartbeat()
    {
        var client = new InMemoryModbusClient();
        await client.ConnectAsync(CancellationToken.None);
        var scenario = new SimulationScenario(
            Array.Empty<SimulationEvent>(),
            new SimulationHeartbeat(SimulationPoints.Heartbeat, TimeSpan.FromSeconds(1)));
        var runner = new SimulationScenarioRunner(scenario, client);

        runner.Advance(TimeSpan.FromMilliseconds(500));
        Assert.Equal((ushort)0, await ReadHoldingRegisterAsync(client, SimulationPoints.Heartbeat));

        runner.Advance(TimeSpan.FromMilliseconds(500)); // 1s -> one whole period
        Assert.Equal((ushort)1, await ReadHoldingRegisterAsync(client, SimulationPoints.Heartbeat));

        runner.Advance(TimeSpan.FromMilliseconds(500)); // 1.5s -> still one period
        Assert.Equal((ushort)1, await ReadHoldingRegisterAsync(client, SimulationPoints.Heartbeat));

        runner.Advance(TimeSpan.FromMilliseconds(500)); // 2s -> two whole periods
        Assert.Equal((ushort)2, await ReadHoldingRegisterAsync(client, SimulationPoints.Heartbeat));
    }

    [Fact]
    public async Task Heartbeat_WrapsAroundUInt16_AndIsStillConsistent()
    {
        var client = new InMemoryModbusClient();
        await client.ConnectAsync(CancellationToken.None);
        client.Memory.WriteHoldingRegister(SimulationPoints.Heartbeat, ushort.MaxValue);

        var scenario = new SimulationScenario(
            Array.Empty<SimulationEvent>(),
            new SimulationHeartbeat(SimulationPoints.Heartbeat, TimeSpan.FromSeconds(1)));
        var runner = new SimulationScenarioRunner(scenario, client);

        runner.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal((ushort)0, await ReadHoldingRegisterAsync(client, SimulationPoints.Heartbeat));
    }

    // --- Fault injection: K1-K7 ------------------------------------------------------

    [Theory]
    [InlineData(SimulationFaults.EmergencyStop)]
    [InlineData(SimulationFaults.SafetyDoorOpen)]
    [InlineData(SimulationFaults.SafetyLightCurtain)]
    [InlineData(SimulationFaults.LowAirPressure)]
    [InlineData(SimulationFaults.StopperExtendTimeout)]
    [InlineData(SimulationFaults.StopperNotRetracted)]
    [InlineData(SimulationFaults.ScanTimeout)]
    public async Task FaultInjection_SetsFaultCode_ThenClears(ushort faultCode)
    {
        var client = new InMemoryModbusClient();
        await client.ConnectAsync(CancellationToken.None);
        var scenario = new SimulationScenario(new SimulationEvent[]
        {
            new SetRegisterEvent(TimeSpan.FromSeconds(1), SimulationPoints.FaultCode, faultCode),
            new SetRegisterEvent(TimeSpan.FromSeconds(3), SimulationPoints.FaultCode, SimulationFaults.None),
        });
        var runner = new SimulationScenarioRunner(scenario, client);

        runner.Advance(TimeSpan.FromMilliseconds(999));
        Assert.Equal(SimulationFaults.None, await ReadHoldingRegisterAsync(client, SimulationPoints.FaultCode));

        runner.Advance(TimeSpan.FromMilliseconds(1)); // t=1s: fault raised
        Assert.Equal(faultCode, await ReadHoldingRegisterAsync(client, SimulationPoints.FaultCode));

        runner.Advance(TimeSpan.FromSeconds(1)); // t=2s: fault still latched
        Assert.Equal(faultCode, await ReadHoldingRegisterAsync(client, SimulationPoints.FaultCode));

        runner.Advance(TimeSpan.FromSeconds(1)); // t=3s: fault cleared
        Assert.Equal(SimulationFaults.None, await ReadHoldingRegisterAsync(client, SimulationPoints.FaultCode));
    }

    // --- Timeout (deterministic K7) --------------------------------------------------

    [Fact]
    public async Task Timeout_WaitsForMissingInput_ThenDeterministicallyRaisesK7()
    {
        var client = new InMemoryModbusClient();
        await client.ConnectAsync(CancellationToken.None);
        var scenario = new SimulationScenario(new SimulationEvent[]
        {
            new SetStepEvent(TimeSpan.Zero, 3), // step 3 triggers camera; the X13 done signal never arrives
            new SetRegisterEvent(TimeSpan.FromSeconds(2), SimulationPoints.FaultCode, SimulationFaults.ScanTimeout),
        });
        var runner = new SimulationScenarioRunner(scenario, client);

        runner.Advance(TimeSpan.FromMilliseconds(1999));
        Assert.Equal(SimulationFaults.None, await ReadHoldingRegisterAsync(client, SimulationPoints.FaultCode));

        runner.Advance(TimeSpan.FromMilliseconds(1)); // 2000ms: the scan timeout fires deterministically
        Assert.Equal(SimulationFaults.ScanTimeout, await ReadHoldingRegisterAsync(client, SimulationPoints.FaultCode));
    }

    // --- Disconnect window + recovery -------------------------------------------------

    [Fact]
    public async Task DisconnectWindow_ClientRejectsRequests_AndRecoversAfterReconnect()
    {
        var client = new InMemoryModbusClient();
        var scenario = new SimulationScenario(new SimulationEvent[]
        {
            new ConnectEvent(TimeSpan.Zero),
            new SetStepEvent(TimeSpan.Zero, 0),
            new DisconnectEvent(TimeSpan.FromSeconds(1)),
            new SetStepEvent(TimeSpan.FromSeconds(2), 1), // PLC keeps running while offline
            new ConnectEvent(TimeSpan.FromSeconds(3)),
        });
        var runner = new SimulationScenarioRunner(scenario, client);

        runner.Advance(TimeSpan.Zero);
        Assert.Equal((ushort)0, await ReadHoldingRegisterAsync(client, SimulationPoints.StepRegister));

        runner.Advance(TimeSpan.FromSeconds(1)); // disconnect: client rejects every request
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.ReadHoldingRegistersAsync(1, SimulationPoints.StepRegister, 1, CancellationToken.None));

        runner.Advance(TimeSpan.FromSeconds(2)); // t=3s: reconnected
        Assert.Equal((ushort)1, await ReadHoldingRegisterAsync(client, SimulationPoints.StepRegister));
    }

    // --- Production counter D138 single word -------------------------------------

    [Fact]
    public async Task Scenario_DrivesProductionCounter_SingleWord()
    {
        var client = new InMemoryModbusClient();
        await client.ConnectAsync(CancellationToken.None);
        var scenario = new SimulationScenario(new SimulationEvent[]
        {
            new SetRegisterEvent(TimeSpan.FromSeconds(1), SimulationPoints.Production, 5),
            new SetRegisterEvent(TimeSpan.FromSeconds(2), SimulationPoints.Production, 6),
        });
        var runner = new SimulationScenarioRunner(scenario, client);

        runner.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal((ushort)5, await ReadHoldingRegisterAsync(client, SimulationPoints.Production));

        runner.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal((ushort)6, await ReadHoldingRegisterAsync(client, SimulationPoints.Production));
    }

    // --- Fake-clock injection / validation ---------------------------------------------

    [Fact]
    public void Advance_NegativeDelta_ThrowsArgumentOutOfRange()
    {
        var client = new InMemoryModbusClient();
        var scenario = new SimulationScenario(Array.Empty<SimulationEvent>());
        var runner = new SimulationScenarioRunner(scenario, client, new SimulationClock());

        Assert.Throws<ArgumentOutOfRangeException>(() => runner.Advance(TimeSpan.FromMilliseconds(-1)));
    }

    [Fact]
    public void Heartbeat_ZeroPeriod_ThrowsArgumentOutOfRange()
        => Assert.Throws<ArgumentOutOfRangeException>(
            () => new SimulationHeartbeat(SimulationPoints.Heartbeat, TimeSpan.Zero));

    [Fact]
    public void Scenario_NullEvents_ThrowsArgumentNull()
        => Assert.Throws<ArgumentNullException>(() => new SimulationScenario(null!));

    // --- Review fixes: unknown event default, null entries, negative At, heartbeat init-bypass -----

    [Fact]
    public void Apply_UnknownEventType_ThrowsInvalidOperation_NamingTheType()
    {
        var client = new InMemoryModbusClient();
        var scenario = new SimulationScenario(new SimulationEvent[]
        {
            new UnknownSimulationEvent(TimeSpan.Zero),
        });
        var runner = new SimulationScenarioRunner(scenario, client);

        var ex = Assert.Throws<InvalidOperationException>(() => runner.Advance(TimeSpan.Zero));
        Assert.Contains(nameof(UnknownSimulationEvent), ex.Message);
    }

    [Fact]
    public void Scenario_NullEventEntry_ThrowsArgumentException()
        => Assert.Throws<ArgumentException>(() => new SimulationScenario(new SimulationEvent[] { null! }));

    [Fact]
    public void Heartbeat_InitBypassZeroPeriod_ThrowsArgumentOutOfRange()
        => Assert.Throws<ArgumentOutOfRangeException>(
            () => new SimulationHeartbeat(SimulationPoints.Heartbeat, TimeSpan.FromSeconds(1))
            {
                Period = TimeSpan.Zero,
            });

    [Fact]
    public void SetStepEvent_NegativeAt_ThrowsArgumentOutOfRange()
        => Assert.Throws<ArgumentOutOfRangeException>(
            () => new SetStepEvent(TimeSpan.FromMilliseconds(-1), 0));

    private sealed record UnknownSimulationEvent(TimeSpan at) : SimulationEvent(at);
}
