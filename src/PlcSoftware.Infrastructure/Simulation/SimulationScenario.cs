namespace PlcSoftware.Infrastructure.Simulation;

/// <summary>
/// The zero-based protocol addresses the simulated profile exposes for the supervisory-control data
/// points the automatic flow and monitoring use. These match the <c>point-map.simulation.json</c>
/// profile (M points are coils, D points are holding registers at their profile protocol addresses;
/// M200-M205 are the per-step flags, D102 the packed M200-M215 bit-field register, D200 the current
/// step number, D101 the heartbeat, D110 the fault code and D207/D208 the low/high production-counter
/// words).
/// </summary>
public static class SimulationPoints
{
    /// <summary>D101, the PLC heartbeat counter (increments every heartbeat period).</summary>
    public const ushort Heartbeat = 0x0001;

    /// <summary>D102, the packed register mirroring M200-M215 (bit i = M(200+i); M200 = bit0).</summary>
    public const ushort StepBitsRegister = 0x0002;

    /// <summary>D110, the fault code register, where 0 = no fault and 1..7 = K1..K7.</summary>
    public const ushort FaultCode = 0x000A;

    /// <summary>D200, the current automatic-flow step number (0..5).</summary>
    public const ushort StepRegister = 0x0064;

    /// <summary>D207, the production-counter low word (least-significant 16 bits).</summary>
    public const ushort ProductionLow = 0x006B;

    /// <summary>D208, the production-counter high word (most-significant 16 bits).</summary>
    public const ushort ProductionHigh = 0x006C;

    /// <summary>M200, the coil of the first step flag (步骤0等待进板).</summary>
    public const ushort FirstStepFlag = 0x00C8;

    /// <summary>Number of step-flag coils, M200..M205, covering steps 0..5.</summary>
    public const ushort StepFlagCount = 6;
}

/// <summary>
/// D110 fault codes per the design (§6.7). <c>0</c> means no fault; the K1-K7 codes raise alarms on
/// the HMI when D110 goes 0 → non-zero and clear when it returns to 0.
/// </summary>
public static class SimulationFaults
{
    public const ushort None = 0;
    public const ushort EmergencyStop = 1;        // K1 急停
    public const ushort SafetyDoorOpen = 2;       // K2 安全门打开
    public const ushort SafetyLightCurtain = 3;   // K3 安全光栅
    public const ushort LowAirPressure = 4;       // K4 气压低
    public const ushort StopperExtendTimeout = 5; // K5 气缸挡停伸出超时
    public const ushort StopperNotRetracted = 6;  // K6 挡停未缩回
    public const ushort ScanTimeout = 7;          // K7 扫码超时
}

/// <summary>
/// A single scheduled event in a <see cref="SimulationScenario"/>. Every event carries the virtual
/// time (<see cref="At"/>) at which the <see cref="SimulationScenarioRunner"/> applies it; applying an
/// event is purely a function of that virtual time, never of a real wall clock. The virtual time cannot
/// be negative — an event scheduled before the start of the run is invalid.
/// </summary>
public abstract record SimulationEvent
{
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="at"/> is negative.</exception>
    protected SimulationEvent(TimeSpan at)
    {
        if (at < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(at), at, "Event time cannot be negative.");
        }

        At = at;
    }

    /// <summary>The virtual time at which the event is applied.</summary>
    public TimeSpan At { get; init; }
}

/// <summary>Writes a single holding register (semantically FC06). Used e.g. for D110 / D207 / D208.</summary>
public sealed record SetRegisterEvent(TimeSpan At, ushort Address, ushort Value) : SimulationEvent(At);

/// <summary>Writes a single coil (semantically FC05). Used e.g. for the M200-M205 step flags.</summary>
public sealed record SetCoilEvent(TimeSpan At, ushort Address, bool Value) : SimulationEvent(At);

/// <summary>
/// Advances the automatic flow to <paramref name="Step"/> (0..5): writes the D200 step register and
/// makes exactly the M(200+step) coil true while clearing the other step-flag coils.
/// </summary>
public sealed record SetStepEvent(TimeSpan At, ushort Step) : SimulationEvent(At);

/// <summary>Takes the simulated client offline; it rejects all requests until a <see cref="ConnectEvent"/>.</summary>
public sealed record DisconnectEvent(TimeSpan At) : SimulationEvent(At);

/// <summary>Brings the simulated client back online after a <see cref="DisconnectEvent"/>.</summary>
public sealed record ConnectEvent(TimeSpan At) : SimulationEvent(At);

/// <summary>
/// Configures a repeating heartbeat: the register at <paramref name="Address"/> (D101) is incremented
/// once per whole <paramref name="Period"/> of elapsed virtual time. The first increment is at the end
/// of the first full period. Increments are pure integer ticks of virtual time so they are deterministic.
/// The period must be positive.
/// </summary>
public sealed record SimulationHeartbeat
{
    private readonly TimeSpan _period;

    public SimulationHeartbeat(ushort address, TimeSpan period)
    {
        if (period <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(period), period, "Heartbeat period must be positive.");
        }

        Address = address;
        Period = period;
    }

    /// <summary>The register address to increment (D101).</summary>
    public ushort Address { get; init; }

    /// <summary>Period between increments; one increment per whole elapsed period.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is not positive.</exception>
    public TimeSpan Period
    {
        get => _period;
        init
        {
            if (value <= TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(nameof(Period), value, "Heartbeat period must be positive.");
            }

            _period = value;
        }
    }
}

/// <summary>
/// A declarative, replayable recipe for a simulated PLC run: an ordered schedule of
/// <see cref="SimulationEvent"/>s (sortable by their virtual timestamp) plus an optional periodic
/// heartbeat. It deliberately knows nothing about the wall clock — replaying it is a function of the
/// virtual time the <see cref="SimulationScenarioRunner"/> is advanced to.
/// </summary>
public sealed class SimulationScenario
{
    /// <summary>Creates a scenario from an event schedule and an optional repeating heartbeat.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="events"/> is <c>null</c>.</exception>
    /// <exception cref="ArgumentException"><paramref name="events"/> contains a <c>null</c> entry.</exception>
    public SimulationScenario(IEnumerable<SimulationEvent> events, SimulationHeartbeat? heartbeat = null)
    {
        ArgumentNullException.ThrowIfNull(events);
        Events = events.ToList();
        if (Events.Any(e => e is null))
        {
            throw new ArgumentException("Event list cannot contain null entries.", nameof(events));
        }

        Heartbeat = heartbeat;
    }

    /// <summary>The scheduled events, applied chronologically (any registration order is normalised).</summary>
    public IReadOnlyList<SimulationEvent> Events { get; }

    /// <summary>Optional repeating heartbeat configuration (e.g. D101), or <c>null</c> if none.</summary>
    public SimulationHeartbeat? Heartbeat { get; }
}
