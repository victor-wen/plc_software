using PlcSoftware.Core.Abstractions;

namespace PlcSoftware.Infrastructure.Simulation;

/// <summary>
/// The clock the scenario runner advances over. It is deliberately a bare virtual-time abstraction so a
/// test can inject any implementation — nothing reads <see cref="System.DateTime"/>/<c>Stopwatch</c>, so
/// scenario transitions are a pure, deterministic function of the time that was advanced.
/// </summary>
public interface ISimulationClock
{
    /// <summary>The current virtual time, advancing monotonically from <see cref="TimeSpan.Zero"/>.</summary>
    TimeSpan Current { get; }

    /// <summary>Advances the virtual time by <paramref name="delta"/>.</summary>
    void Advance(TimeSpan delta);
}

/// <summary>
/// The default deterministic clock: it only holds an accumulated <see cref="Current"/> that a caller
/// advances by hand. No real system time is ever consulted.
/// </summary>
public sealed class SimulationClock : ISimulationClock
{
    /// <inheritdoc />
    public TimeSpan Current { get; private set; } = TimeSpan.Zero;

    /// <inheritdoc />
    public void Advance(TimeSpan delta) => Current += delta;
}

/// <summary>
/// Replays a <see cref="SimulationScenario"/> onto an <see cref="InMemoryModbusClient"/>'s
/// <see cref="SimulationMemory"/> over a deterministic, manually-advanced clock.
///
/// Time is fully virtual: callers advance the clock via <see cref="Advance"/>, and every scenario event
/// whose <see cref="SimulationEvent.At"/> falls at or before the new time is applied in chronological
/// order (once, idempotently). The runner never delays, sleeps or reads a wall clock — re-running the
/// same advances on the same scenario plus memory always yields the identical end state.
///
/// Connection lifecycle: <see cref="ConnectEvent"/> / <see cref="DisconnectEvent"/> drive the client
/// online/offline so an offline window makes the client reject (or the HMI freeze) requests, while the
/// underlying automatic-flow logic keeps mutating memory — reproducing the "断线冻结快照、PLC 继续" design.
/// </summary>
public sealed class SimulationScenarioRunner
{
    private readonly InMemoryModbusClient _client;
    private readonly ISimulationClock _clock;
    private readonly IReadOnlyList<SimulationEvent> _events;
    private readonly SimulationHeartbeat? _heartbeat;
    private int _nextEventIndex;
    private long _heartbeatTicksApplied;

    /// <summary>Creates a runner over <paramref name="scenario"/> driving <paramref name="client"/>.</summary>
    public SimulationScenarioRunner(
        SimulationScenario scenario,
        InMemoryModbusClient client,
        ISimulationClock? clock = null)
    {
        ArgumentNullException.ThrowIfNull(scenario);
        ArgumentNullException.ThrowIfNull(client);

        _client = client;
        _clock = clock ?? new SimulationClock();
        _events = scenario.Events.OrderBy(e => e.At).ToList();
        _heartbeat = scenario.Heartbeat;
    }

    /// <summary>The current virtual time.</summary>
    public TimeSpan CurrentTime => _clock.Current;

    /// <summary>
    /// The client the runner drives, exposed over the transport-neutral <see cref="IModbusClient"/>
    /// surface only (Gate-2: the device is reachable solely through <c>IModbusClient</c>). Consumers
    /// read simulated state via the read function codes; the concrete <see cref="InMemoryModbusClient"/>
    /// (and its raw <see cref="InMemoryModbusClient.Memory"/>) is kept internal to the engine for seeding.
    /// </summary>
    public IModbusClient Client => _client;

    /// <summary>The clock (injectable for tests); advancing it is driven solely by <see cref="Advance"/>.</summary>
    public ISimulationClock Clock => _clock;

    /// <summary>
    /// Advances virtual time by <paramref name="delta"/> and applies every not-yet-applied event scheduled
    /// at or before the new time, in chronological order, plus any heartbeat periods that elapsed.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="delta"/> is negative.</exception>
    public void Advance(TimeSpan delta)
    {
        if (delta < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(delta), delta, "Virtual time cannot move backwards.");
        }

        _clock.Advance(delta);
        ApplyHeartbeat();
        ApplyPendingEvents();
    }

    private void ApplyPendingEvents()
    {
        // Apply, in chronological order, every event scheduled at or before the current virtual time
        // that has not yet been applied. The monotonic index guarantees an event is applied exactly
        // once even if the clock is advanced in small or large jumps.
        while (_nextEventIndex < _events.Count && _events[_nextEventIndex].At <= _clock.Current)
        {
            Apply(_events[_nextEventIndex]);
            _nextEventIndex++;
        }
    }

    private void Apply(SimulationEvent simulationEvent)
    {
        switch (simulationEvent)
        {
            case SetRegisterEvent set:
                _client.Memory.WriteHoldingRegister(set.Address, set.Value);
                break;
            case SetCoilEvent set:
                _client.Memory.WriteCoil(set.Address, set.Value);
                break;
            case SetStepEvent set:
                ApplyStep(set.Step);
                break;
            case DisconnectEvent:
                // The in-memory client completes synchronously, so blocking is safe and deterministic.
                _client.DisconnectAsync(CancellationToken.None).GetAwaiter().GetResult();
                break;
            case ConnectEvent:
                _client.ConnectAsync(CancellationToken.None).GetAwaiter().GetResult();
                break;
            default:
                throw new InvalidOperationException(
                    $"Unknown simulation event type: {simulationEvent.GetType().Name}.");
        }
    }

    private void ApplyStep(ushort step)
    {
        if (step >= SimulationPoints.StepFlagCount)
        {
            throw new ArgumentOutOfRangeException(nameof(step), step, "Step must be 0..StepFlagCount-1.");
        }

        _client.Memory.WriteHoldingRegister(SimulationPoints.StepRegister, step);
        _client.Memory.WriteHoldingRegister(SimulationPoints.StepBitsRegister, (ushort)(1 << step));
        for (ushort i = 0; i < SimulationPoints.StepFlagCount; i++)
        {
            _client.Memory.WriteCoil((ushort)(SimulationPoints.FirstStepFlag + i), i == step);
        }
    }

    private void ApplyHeartbeat()
    {
        if (_heartbeat is null)
        {
            return;
        }

        var totalTicks = _clock.Current.Ticks / _heartbeat.Period.Ticks;
        if (totalTicks <= _heartbeatTicksApplied)
        {
            return;
        }

        var increment = totalTicks - _heartbeatTicksApplied;
        var current = _client.Memory.ReadHoldingRegisters(_heartbeat.Address, 1)[0];
        _client.Memory.WriteHoldingRegister(_heartbeat.Address, (ushort)(current + increment));
        _heartbeatTicksApplied = totalTicks;
    }
}
