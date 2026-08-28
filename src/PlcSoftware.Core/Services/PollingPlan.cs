namespace PlcSoftware.Core.Services;

/// <summary>
/// Which Modbus area a <see cref="PollingGroup"/> reads, selecting the matching
/// <see cref="Abstractions.IModbusClient"/> read function:
/// <see cref="Abstractions.IModbusClient.ReadHoldingRegistersAsync"/> (PLC D registers),
/// <see cref="Abstractions.IModbusClient.ReadCoilsAsync"/> (PLC Y outputs) or
/// <see cref="Abstractions.IModbusClient.ReadDiscreteInputsAsync"/> (PLC X inputs).
/// </summary>
public enum PollingArea
{
    /// <summary>FC03 — read holding registers (PLC D data registers).</summary>
    HoldingRegisters,

    /// <summary>FC01 — read coils (PLC Y outputs).</summary>
    Coils,

    /// <summary>FC02 — read discrete inputs (PLC X inputs).</summary>
    DiscreteInputs,
}

/// <summary>
/// One declarative polling group: a single contiguous block read on a fixed interval from one slave.
/// Together the groups make up a <see cref="PollingPlan"/>.
///
/// <see cref="StartAddress"/> is the zero-based Modbus protocol address (offset-based, per the point
/// map) and <see cref="Count"/> is the number of points read in one request. All reads of every group
/// go through the single shared <see cref="Abstractions.IModbusClient"/>, so in production they are
/// serialised in one queue underneath (see the <c>QueuedModbusClient</c> decorator).
/// </summary>
public sealed class PollingGroup
{
    /// <summary>Builds a group. <paramref name="interval"/> and <paramref name="count"/> must be positive.</summary>
    public PollingGroup(
        string name,
        TimeSpan interval,
        byte slaveId,
        ushort startAddress,
        ushort count,
        PollingArea area = PollingArea.HoldingRegisters)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("Group name must not be empty.", nameof(name));
        }

        if (interval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(interval), interval, "must be positive.");
        }

        if (count == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(count), count, "must be greater than 0.");
        }

        Name = name;
        Interval = interval;
        SlaveId = slaveId;
        StartAddress = startAddress;
        Count = count;
        Area = area;
    }

    /// <summary>Human-readable group name (e.g. "Fast", "Process", "Params", "Io").</summary>
    public string Name { get; }

    /// <summary>Nominal interval between reads of this group.</summary>
    public TimeSpan Interval { get; }

    /// <summary>Modbus slave (unit) id.</summary>
    public byte SlaveId { get; }

    /// <summary>Zero-based Modbus protocol start address (offset, per the point map).</summary>
    public ushort StartAddress { get; }

    /// <summary>Number of points read per request.</summary>
    public ushort Count { get; }

    /// <summary>Modbus area / function code for this group's read.</summary>
    public PollingArea Area { get; }
}

/// <summary>
/// Declarative polling plan: the ordered list of groups the <see cref="PollingService"/> executes.
///
/// <see cref="Default"/> encodes the design groups (updated to the new register map):
///   <list type="bullet">
///     <item><b>Fast</b> — every 250 ms reads the D100-D110 block (protocol addresses 0-10).</item>
///     <item><b>Process</b> — every 500 ms reads the D120-D140 block (protocol addresses 20-40, 21 registers).</item>
///     <item><b>Params</b> — every 500 ms reads the D204-D220 block (protocol addresses 104-120, 17 registers).</item>
///     <item><b>Io</b> — every 500 ms reads the X input block (protocol addresses 0-23, 24 discrete inputs).</item>
///     <item><b>Io.Y</b> — every 500 ms reads the Y output (coil) block (protocol addresses 0-23, 24 coils).</item>
///   </list>
/// Only reads and writes that belong to the process/command surface are modelled here; each group's
/// payload is decoded by a later layer, not by the polling service itself.
/// </summary>
public sealed class PollingPlan
{
    /// <summary>Builds a plan. <paramref name="groups"/> must contain at least one group.</summary>
    public PollingPlan(IReadOnlyList<PollingGroup> groups)
    {
        if (groups is null)
        {
            throw new ArgumentNullException(nameof(groups));
        }

        var snapshot = groups.ToArray();
        if (snapshot.Length == 0)
        {
            throw new ArgumentException("Plan must contain at least one polling group.", nameof(groups));
        }

        if (snapshot.Any(g => g is null))
        {
            throw new ArgumentException("Plan must not contain null groups.", nameof(groups));
        }

        Groups = snapshot;
    }

    /// <summary>The grouped read schedule, in declaration order.</summary>
    public IReadOnlyList<PollingGroup> Groups { get; }

    /// <summary>
    /// The production plan for the supervisory control (see the type summary). Slave id 1 is the
    /// configured target; addresses are the offset-based protocol addresses from the point map.
    /// </summary>
    public static PollingPlan Default()
        => new(new[]
        {
            // D100-D110 → protocol addresses 0-10 (11 registers).
            new PollingGroup("Fast", TimeSpan.FromMilliseconds(250), 1, 0, 11),
            // D120-D140 → protocol addresses 20-40 (21 registers: D120/122/124/126/128/130/136/138/140).
            new PollingGroup("Process", TimeSpan.FromMilliseconds(500), 1, 20, 21),
            // D204-D220 → protocol addresses 104-120 (17 registers: D204/D210/D220).
            new PollingGroup("Params", TimeSpan.FromMilliseconds(500), 1, 104, 17),
            // X0-X27 → protocol addresses 0-23 (24 discrete inputs, octal 0-27 → decimal 0-23).
            new PollingGroup("Io", TimeSpan.FromMilliseconds(500), 1, 0, 24, PollingArea.DiscreteInputs),
            // Y0-Y27 → protocol addresses 0-23 (24 coils).
            new PollingGroup("Io.Y", TimeSpan.FromMilliseconds(500), 1, 0, 24, PollingArea.Coils),
        });
}
