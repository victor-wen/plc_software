using PlcSoftware.Core.Abstractions;

namespace PlcSoftware.Infrastructure.Simulation;

/// <summary>
/// Backing memory for a simulated PLC's four Modbus data areas: coils (FC01/FC05), discrete inputs
/// (FC02), holding registers (FC03/FC06) and input registers (FC04). Each area spans the full
/// 16-bit Modbus address space (<see cref="ModbusLimits.AddressSpaceSize"/>) so any valid protocol
/// address is indexable.
///
/// This is a dumb store: it performs no argument validation (that is the responsibility of the
/// client, which follows the <c>ModbusContractTests</c> rules) and no concurrency control. Coils and
/// holding registers are host-writable (FC05/FC06); discrete inputs and input registers represent
/// field-side values that the simulation engine seeds via <see cref="WriteDiscreteInput"/> /
/// <see cref="WriteInputRegister"/>.
///
/// Thread ownership: an instance is <b>single-threaded by design</b> — it provides no internal
/// synchronization and is not safe for concurrent access. The owning simulation engine must serialize
/// all reads and writes onto one thread/loop; readers and writers must never touch the same memory
/// from different threads simultaneously.
/// </summary>
public sealed class SimulationMemory
{
    private readonly bool[] _coils = new bool[ModbusLimits.AddressSpaceSize];
    private readonly bool[] _discreteInputs = new bool[ModbusLimits.AddressSpaceSize];
    private readonly ushort[] _holdingRegisters = new ushort[ModbusLimits.AddressSpaceSize];
    private readonly ushort[] _inputRegisters = new ushort[ModbusLimits.AddressSpaceSize];

    /// <summary>Reads <paramref name="count"/> coils starting at <paramref name="address"/> (FC01).</summary>
    public bool[] ReadCoils(ushort address, ushort count)
        => CopyBits(_coils, address, count);

    /// <summary>Reads <paramref name="count"/> discrete inputs starting at <paramref name="address"/> (FC02).</summary>
    public bool[] ReadDiscreteInputs(ushort address, ushort count)
        => CopyBits(_discreteInputs, address, count);

    /// <summary>Reads <paramref name="count"/> holding registers starting at <paramref name="address"/> (FC03).</summary>
    public ushort[] ReadHoldingRegisters(ushort address, ushort count)
        => CopyRegisters(_holdingRegisters, address, count);

    /// <summary>Reads <paramref name="count"/> input registers starting at <paramref name="address"/> (FC04).</summary>
    public ushort[] ReadInputRegisters(ushort address, ushort count)
        => CopyRegisters(_inputRegisters, address, count);

    /// <summary>Writes a single coil value at <paramref name="address"/> (FC05).</summary>
    public void WriteCoil(ushort address, bool value)
        => _coils[address] = value;

    /// <summary>Writes a single holding-register value at <paramref name="address"/> (FC06).</summary>
    public void WriteHoldingRegister(ushort address, ushort value)
        => _holdingRegisters[address] = value;

    /// <summary>Seeds a single discrete-input value at <paramref name="address"/> (simulated field input).</summary>
    public void WriteDiscreteInput(ushort address, bool value)
        => _discreteInputs[address] = value;

    /// <summary>Seeds a single input-register value at <paramref name="address"/> (simulated field input).</summary>
    public void WriteInputRegister(ushort address, ushort value)
        => _inputRegisters[address] = value;

    private static bool[] CopyBits(bool[] source, ushort address, ushort count)
    {
        var result = new bool[count];
        Array.Copy(source, address, result, 0, count);
        return result;
    }

    private static ushort[] CopyRegisters(ushort[] source, ushort address, ushort count)
    {
        var result = new ushort[count];
        Array.Copy(source, address, result, 0, count);
        return result;
    }
}
