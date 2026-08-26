namespace PlcSoftware.Core.Abstractions;

/// <summary>
/// Transport-independent Modbus/TCP &amp; Modbus/RTU client contract.
///
/// Implementations own connection lifecycle, a single request queue, and per-call
/// cancellation. Argument validation (count bounds, address-space overflow) and cancellation
/// propagation are pinned by <c>ModbusContractTests</c>; callers may rely on it.
///
/// Client units are the low-level Modbus data types (bool for bits, ushort for registers),
/// not PLC-area-encoded values. Mapping of PLC areas (X/Y/M/D) to function codes and
/// protocol addresses is done above this abstraction (see the point map / decoder layer).
/// </summary>
public interface IModbusClient : IAsyncDisposable
{
    Task ConnectAsync(CancellationToken cancellationToken);
    Task DisconnectAsync(CancellationToken cancellationToken);
    Task<bool[]> ReadCoilsAsync(byte slaveId, ushort address, ushort count, CancellationToken cancellationToken);
    Task<bool[]> ReadDiscreteInputsAsync(byte slaveId, ushort address, ushort count, CancellationToken cancellationToken);
    Task<ushort[]> ReadHoldingRegistersAsync(byte slaveId, ushort address, ushort count, CancellationToken cancellationToken);
    Task<ushort[]> ReadInputRegistersAsync(byte slaveId, ushort address, ushort count, CancellationToken cancellationToken);
    Task WriteSingleCoilAsync(byte slaveId, ushort address, bool value, CancellationToken cancellationToken);
    Task WriteSingleRegisterAsync(byte slaveId, ushort address, ushort value, CancellationToken cancellationToken);
}
