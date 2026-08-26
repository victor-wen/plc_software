namespace PlcSoftware.Core.Abstractions;

/// <summary>
/// Minimal, transport-agnostic connection facade driven by <c>ConnectionSupervisor</c>. It exposes only
/// lifecycle (<see cref="ConnectAsync"/> / <see cref="DisconnectAsync"/>) and liveness
/// (<see cref="ProbeAsync"/>) operations.
///
/// <para>Every method observes the <c>cancellationToken</c> and surfaces cancellation as
/// <see cref="OperationCanceledException"/>. The supervisor additionally bounds each call with a
/// per-operation timeout; an implementation is expected to cancel promptly when its token is cancelled
/// rather than rely on the supervisor to abandon the wait.</para>
///
/// <para>Structural guarantee: there is deliberately no write path here, so the supervisor cannot
/// re-submit a write command by construction. <c>ConnectionSupervisor</c> surfaces only connect /
/// disconnect / probe. Any guarantee about queued host writes being cancelled on disconnect is owned by
/// a later wiring task and is intentionally not asserted here.</para>
/// </summary>
public interface ISupervisedConnection
{
    Task ConnectAsync(CancellationToken cancellationToken);

    Task DisconnectAsync(CancellationToken cancellationToken);

    /// <summary>Probes the link. Returns <c>true</c> when the peer is alive, <c>false</c> when dead.</summary>
    Task<bool> ProbeAsync(CancellationToken cancellationToken);
}
