using PlcSoftware.Core.Abstractions;

namespace PlcSoftware.Infrastructure.Simulation;

/// <summary>
/// In-memory implementation of <see cref="IModbusClient"/> built over a <see cref="SimulationMemory"/>.
/// It emulates the Modbus function codes relevant to the supervisory-control loop — FC01/02/03/04
/// (reads) and FC05/06 (single-point writes) — without any transport I/O.
///
/// The client owns connection lifecycle: it starts disconnected, <see cref="ConnectAsync"/> brings it
/// online, <see cref="DisconnectAsync"/> takes it offline, and <see cref="DisposeAsync"/> releases the
/// instance. Any request to a disconnected (or disposed) client is rejected. Argument validation and
/// cancellation propagation follow the rules pinned by <c>ModbusContractTests</c>: cancellation is
/// honoured first, then reads must satisfy <see cref="ModbusLimits"/> count/address-space bounds.
///
/// The <c>slaveId</c> argument is accepted but ignored — there is a single simulated device.
///
/// Ownership: like <see cref="SimulationMemory"/>, a client is single-owner and not thread-safe.
/// The connected/disposed state flags are intentionally <b>not</b> declared <c>volatile</c>: they are
/// written and read only from the one simulation-engine loop that owns this instance, so no
/// multi-threaded memory-ordering guarantees are required. Concurrent access from multiple threads
/// is not supported.
/// </summary>
public sealed class InMemoryModbusClient : IModbusClient
{
    private readonly SimulationMemory _memory;
    private bool _connected;
    private bool _disposed;

    public InMemoryModbusClient()
        : this(new SimulationMemory())
    {
    }

    public InMemoryModbusClient(SimulationMemory memory)
    {
        _memory = memory ?? throw new ArgumentNullException(nameof(memory));
    }

    /// <summary>Backing memory, exposed so the simulation engine can seed field-side inputs.</summary>
    public SimulationMemory Memory => _memory;

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(InMemoryModbusClient));
        }
    }

    public Task ConnectAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfDisposed();
        _connected = true;
        return Task.CompletedTask;
    }

    public Task DisconnectAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfDisposed();
        _connected = false;
        return Task.CompletedTask;
    }

    public Task<bool[]> ReadCoilsAsync(byte slaveId, ushort address, ushort count, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureConnected();
        ValidateReadRange(address, count, ModbusLimits.MaxBitsPerRead);
        return Task.FromResult(_memory.ReadCoils(address, count));
    }

    public Task<bool[]> ReadDiscreteInputsAsync(byte slaveId, ushort address, ushort count, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureConnected();
        ValidateReadRange(address, count, ModbusLimits.MaxBitsPerRead);
        return Task.FromResult(_memory.ReadDiscreteInputs(address, count));
    }

    public Task<ushort[]> ReadHoldingRegistersAsync(byte slaveId, ushort address, ushort count, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureConnected();
        ValidateReadRange(address, count, ModbusLimits.MaxRegistersPerRead);
        return Task.FromResult(_memory.ReadHoldingRegisters(address, count));
    }

    public Task<ushort[]> ReadInputRegistersAsync(byte slaveId, ushort address, ushort count, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureConnected();
        ValidateReadRange(address, count, ModbusLimits.MaxRegistersPerRead);
        return Task.FromResult(_memory.ReadInputRegisters(address, count));
    }

    // A single-point write has no count and a ushort address is inherently in the 16-bit space, so
    // there is nothing to validate beyond the already-compliant parameter types (same as the
    // conforming reference in the contract tests).
    public Task WriteSingleCoilAsync(byte slaveId, ushort address, bool value, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureConnected();
        _memory.WriteCoil(address, value);
        return Task.CompletedTask;
    }

    public Task WriteSingleRegisterAsync(byte slaveId, ushort address, ushort value, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureConnected();
        _memory.WriteHoldingRegister(address, value);
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        _disposed = true;
        _connected = false;
        return ValueTask.CompletedTask;
    }

    private void EnsureConnected()
    {
        ThrowIfDisposed();

        if (!_connected)
        {
            throw new InvalidOperationException("The Modbus client is not connected.");
        }
    }

    private static void ValidateReadRange(ushort address, ushort count, int maxCount)
    {
        if (count == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(count), count, "count must be greater than 0.");
        }

        if (count > maxCount)
        {
            throw new ArgumentOutOfRangeException(nameof(count), count, $"count must be no greater than {maxCount}.");
        }

        if (address + count > ModbusLimits.AddressSpaceSize)
        {
            throw new ArgumentOutOfRangeException(
                nameof(address),
                address,
                $"address + count must not exceed the {ModbusLimits.AddressSpaceSize}-wide Modbus address space.");
        }
    }
}
