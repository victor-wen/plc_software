using PlcSoftware.Core.Abstractions;

namespace PlcSoftware.App.Services;

/// <summary>
/// Adapts an <see cref="IModbusClient"/> to the <see cref="ISupervisedConnection"/> surface the
/// <c>ConnectionSupervisor</c> drives. <see cref="ProbeAsync"/> reads a single input register as a
/// liveness probe (true = the peer answered). The supervisor already bounds every call with its own
/// per-operation timeout, so a slow probe simply surfaces as a failed probe.
/// </summary>
internal sealed class ModbusSupervisedConnection : ISupervisedConnection
{
    private readonly IModbusClient _client;
    private readonly byte _slaveId;

    public ModbusSupervisedConnection(IModbusClient client)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _slaveId = 1; // The point-map target slave id (design §5.1).
    }

    public Task ConnectAsync(CancellationToken cancellationToken) => _client.ConnectAsync(cancellationToken);

    public Task DisconnectAsync(CancellationToken cancellationToken) => _client.DisconnectAsync(cancellationToken);

    public async Task<bool> ProbeAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _client.ReadInputRegistersAsync(_slaveId, 0, 1, cancellationToken);
            return true;
        }
        catch (OperationCanceledException)
        {
            // The contract requires cancellation to surface as OCE, so a supervisor timeout is
            // observable; the supervisor decides whether that is shutdown or a failed probe.
            throw;
        }
        catch
        {
            // A transport error means the peer is not reachable — the link is not alive.
            return false;
        }
    }
}
