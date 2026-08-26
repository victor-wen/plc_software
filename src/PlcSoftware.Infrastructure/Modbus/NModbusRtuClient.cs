using NModbus;
using NModbus.IO;
using PlcSoftware.Core.Abstractions;
using PlcSoftware.Core.Configuration;

namespace PlcSoftware.Infrastructure.Modbus;

/// <summary>
/// Modbus/RTU client over a <see cref="System.IO.Ports.SerialPort"/> built on NModbus.
///
/// The client owns the full transport lifecycle as a single unit: <see cref="ConnectAsync"/> asks the
/// injectable <see cref="ISerialPortFactory"/> for an NModbus <see cref="IStreamResource"/> (a real
/// serial port in production, an in-memory or other controlled transport in tests), builds the RTU
/// transport and master over it; <see cref="DisconnectAsync"/> / <see cref="DisposeAsync"/> release
/// the NModbus master first, then the stream resource, idempotently.
///
/// Request handling follows the rules pinned by <c>ModbusContractTests</c> (cancellation is checked
/// before argument validation; reads respect <see cref="ModbusLimits"/> count/address-space bounds)
/// and adds the RTU slave-id policy: the broadcast address (0) and the reserved range (248-255) are
/// rejected with <see cref="ArgumentOutOfRangeException"/>, so only 1-247 (the range the H3U and
/// other RTU devices use) are accepted.
///
/// Any request on a not-connected or disposed client is rejected. The client is not thread-safe.
/// </summary>
public sealed class NModbusRtuClient : IModbusClient
{
    private readonly ISerialPortFactory _factory;
    private readonly IModbusFactory _modbusFactory;

    private IStreamResource? _resource;
    private IModbusSerialMaster? _master;
    private bool _connected;
    private bool _disposed;

    /// <summary>
    /// Creates a client. When <paramref name="factory"/> is omitted a real
    /// <see cref="SerialPortFactory"/> is used; when <paramref name="modbusFactory"/> is omitted a
    /// default <see cref="ModbusFactory"/> is used.
    /// </summary>
    public NModbusRtuClient(
        SerialConnectionOptions options,
        ISerialPortFactory? factory = null,
        IModbusFactory? modbusFactory = null)
    {
        if (options is null)
        {
            throw new ArgumentNullException(nameof(options));
        }

        _factory = factory ?? new SerialPortFactory(options);
        _modbusFactory = modbusFactory ?? new ModbusFactory();
    }

    public Task ConnectAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfDisposed();

        if (_connected)
        {
            throw new InvalidOperationException("The Modbus client is already connected.");
        }

        var resource = _factory.Create();
        try
        {
            var transport = _modbusFactory.CreateRtuTransport(resource);
            var master = _modbusFactory.CreateMaster(transport);
            _resource = resource;
            _master = master;
            _connected = true;
        }
        catch
        {
            resource.Dispose();
            throw;
        }

        return Task.CompletedTask;
    }

    public Task DisconnectAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfDisposed();
        ReleaseResources();
        return Task.CompletedTask;
    }

    public Task<bool[]> ReadCoilsAsync(byte slaveId, ushort address, ushort count, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureConnected();
        ValidateSlaveId(slaveId);
        ModbusReadRange.Validate(address, count, ModbusLimits.MaxBitsPerRead);
        return _master!.ReadCoilsAsync(slaveId, address, count);
    }

    public Task<bool[]> ReadDiscreteInputsAsync(byte slaveId, ushort address, ushort count, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureConnected();
        ValidateSlaveId(slaveId);
        ModbusReadRange.Validate(address, count, ModbusLimits.MaxBitsPerRead);
        return _master!.ReadInputsAsync(slaveId, address, count);
    }

    public Task<ushort[]> ReadHoldingRegistersAsync(byte slaveId, ushort address, ushort count, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureConnected();
        ValidateSlaveId(slaveId);
        ModbusReadRange.Validate(address, count, ModbusLimits.MaxRegistersPerRead);
        return _master!.ReadHoldingRegistersAsync(slaveId, address, count);
    }

    public Task<ushort[]> ReadInputRegistersAsync(byte slaveId, ushort address, ushort count, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureConnected();
        ValidateSlaveId(slaveId);
        ModbusReadRange.Validate(address, count, ModbusLimits.MaxRegistersPerRead);
        return _master!.ReadInputRegistersAsync(slaveId, address, count);
    }

    public Task WriteSingleCoilAsync(byte slaveId, ushort address, bool value, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureConnected();
        ValidateSlaveId(slaveId);
        return _master!.WriteSingleCoilAsync(slaveId, address, value);
    }

    public Task WriteSingleRegisterAsync(byte slaveId, ushort address, ushort value, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureConnected();
        ValidateSlaveId(slaveId);
        return _master!.WriteSingleRegisterAsync(slaveId, address, value);
    }

    public ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return ValueTask.CompletedTask;
        }

        ReleaseResources();
        _disposed = true;
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Releases the NModbus master first, then the stream resource. Idempotent and safe against
    /// double disposal (both fields are nulled before release). Disposing the master already cascades
    /// to the transport/resource, so releasing the resource afterwards is a safe, explicit close of
    /// the port as required by the lifecycle contract.
    /// </summary>
    private void ReleaseResources()
    {
        var master = _master;
        var resource = _resource;
        _master = null;
        _resource = null;
        _connected = false;

        try
        {
            master?.Dispose();
        }
        finally
        {
            resource?.Dispose();
        }
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(NModbusRtuClient));
        }
    }

    private void EnsureConnected()
    {
        ThrowIfDisposed();

        if (!_connected || _master is null)
        {
            throw new InvalidOperationException("The Modbus client is not connected.");
        }
    }

    /// <summary>
    /// Enforces the RTU slave-id convention: 0 is the broadcast address and 248-255 are reserved,
    /// so only 1-247 are valid (the range used by H3U and other RTU devices).
    /// </summary>
    private static void ValidateSlaveId(byte slaveId)
    {
        if (slaveId < 1 || slaveId > 247)
        {
            throw new ArgumentOutOfRangeException(
                nameof(slaveId),
                slaveId,
                "slaveId must be between 1 and 247 (0 is the broadcast address and 248-255 are reserved).");
        }
    }
}
