using PlcSoftware.Core.Abstractions;

namespace PlcSoftware.Infrastructure.Modbus;

/// <summary>
/// An <see cref="IModbusClient"/> decorator that routes every bus operation through a
/// <see cref="ModbusRequestQueue"/>, guaranteeing that at most one underlying request is in flight at
/// any instant, in FIFO order. This is the single serialisation point called out by the design: all
/// Modbus reads and writes go through the queue, never directly to the transport.
///
/// <para>Lifecycle (<see cref="ConnectAsync"/> / <see cref="DisconnectAsync"/>) is routed through the
/// same single-flight queue, so it is serialised with the read/write backlog: a disconnect queued
/// behind an in-flight read waits for that read to complete before tearing down the transport, and
/// never interrupts a request mid-frame. Argument validation and cancellation-before-validation
/// remain the inner client's responsibility (pinned by <c>ModbusContractTests</c>); this decorator
/// adds the serialisation boundary.</para>
///
/// <para><see cref="DisposeAsync"/> only shuts the queue down; the wrapped client is intentionally
/// not disposed here, because its lifetime is owned by whoever built it (e.g. the transport
/// lifecycle).</para>
///
/// <para>Thread-safe: a <see cref="QueuedModbusClient"/> may be shared across many concurrent
/// callers.</para>
/// </summary>
public sealed class QueuedModbusClient : IModbusClient
{
    private readonly IModbusClient _inner;
    private readonly ModbusRequestQueue _queue;

    /// <summary>Wraps <paramref name="inner"/>, routing every bus operation (read, write, lifecycle) through a new queue.</summary>
    public QueuedModbusClient(IModbusClient inner)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _queue = new ModbusRequestQueue();
    }

    public Task ConnectAsync(CancellationToken cancellationToken)
        => _queue.EnqueueAsync(_inner.ConnectAsync, cancellationToken);

    public Task DisconnectAsync(CancellationToken cancellationToken)
        => _queue.EnqueueAsync(_inner.DisconnectAsync, cancellationToken);

    public Task<bool[]> ReadCoilsAsync(byte slaveId, ushort address, ushort count, CancellationToken cancellationToken)
        => _queue.EnqueueAsync(token => _inner.ReadCoilsAsync(slaveId, address, count, token), cancellationToken);

    public Task<bool[]> ReadDiscreteInputsAsync(byte slaveId, ushort address, ushort count, CancellationToken cancellationToken)
        => _queue.EnqueueAsync(token => _inner.ReadDiscreteInputsAsync(slaveId, address, count, token), cancellationToken);

    public Task<ushort[]> ReadHoldingRegistersAsync(byte slaveId, ushort address, ushort count, CancellationToken cancellationToken)
        => _queue.EnqueueAsync(token => _inner.ReadHoldingRegistersAsync(slaveId, address, count, token), cancellationToken);

    public Task<ushort[]> ReadInputRegistersAsync(byte slaveId, ushort address, ushort count, CancellationToken cancellationToken)
        => _queue.EnqueueAsync(token => _inner.ReadInputRegistersAsync(slaveId, address, count, token), cancellationToken);

    public Task WriteSingleCoilAsync(byte slaveId, ushort address, bool value, CancellationToken cancellationToken)
        => _queue.EnqueueAsync(token => _inner.WriteSingleCoilAsync(slaveId, address, value, token), cancellationToken);

    public Task WriteSingleRegisterAsync(byte slaveId, ushort address, ushort value, CancellationToken cancellationToken)
        => _queue.EnqueueAsync(token => _inner.WriteSingleRegisterAsync(slaveId, address, value, token), cancellationToken);

    /// <summary>Shuts the queue down (cancelling any pending/in-flight work) but leaves the inner client owned by its creator.</summary>
    public ValueTask DisposeAsync() => _queue.DisposeAsync();
}
