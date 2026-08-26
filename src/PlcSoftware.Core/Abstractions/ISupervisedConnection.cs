namespace PlcSoftware.Core.Abstractions;

/// <summary>
/// Minimal, transport-agnostic connection facade driven by <c>ConnectionSupervisor</c>. It exposes only
/// lifecycle (<see cref="ConnectAsync"/> / <see cref="DisconnectAsync"/>) and liveness
/// (<see cref="ProbeAsync"/>) operations.
///
/// <para>Replay guarantee: there is deliberately no write path here. The supervisor can therefore
/// never re-submit a write command after a reconnect. Host-issued writes that were still queued when
/// the link dropped are cancelled by the owning request queue's shutdown on disconnect (see the
/// Infrastructure queue), never replayed by the supervisor. New writes are only issued by callers once
/// the link is back online.</para>
/// </summary>
public interface ISupervisedConnection
{
    Task ConnectAsync(CancellationToken cancellationToken);

    Task DisconnectAsync(CancellationToken cancellationToken);

    /// <summary>Probes the link. Returns <c>true</c> when the peer is alive, <c>false</c> when dead.</summary>
    Task<bool> ProbeAsync(CancellationToken cancellationToken);
}
