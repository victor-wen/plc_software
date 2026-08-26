namespace PlcSoftware.Core.Models;

/// <summary>
/// Modbus function code (FC) as used on the wire and by <see cref="ModbusOperation"/>.
/// </summary>
public enum ModbusFunctionCode : byte
{
    /// <summary>FC01 — read coils.</summary>
    ReadCoils = 1,

    /// <summary>FC02 — read discrete inputs.</summary>
    ReadDiscreteInputs = 2,

    /// <summary>FC03 — read holding registers.</summary>
    ReadHoldingRegisters = 3,

    /// <summary>FC04 — read input registers.</summary>
    ReadInputRegisters = 4,

    /// <summary>FC05 — write a single coil.</summary>
    WriteSingleCoil = 5,

    /// <summary>FC06 — write a single register.</summary>
    WriteSingleRegister = 6,
}

/// <summary>
/// Immutable description of a single Modbus request, for diagnostics and audit logging.
///
/// Read operations populate <see cref="Count"/> and leave <see cref="Value"/> at 0; single-point
/// write operations populate <see cref="Value"/> (a coil value is encoded as 0/1) and leave
/// <see cref="Count"/> at 0. <see cref="Address"/> is the zero-based Modbus protocol address.
/// </summary>
public readonly record struct ModbusOperation(
    byte SlaveId,
    ModbusFunctionCode Function,
    ushort Address,
    ushort Count = 0,
    ushort Value = 0);
