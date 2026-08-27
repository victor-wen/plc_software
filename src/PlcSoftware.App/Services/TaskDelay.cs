using PlcSoftware.Core.Abstractions;

namespace PlcSoftware.App.Services;

/// <summary>
/// Production <see cref="IAsyncDelay"/> built on <see cref="Task.Delay"/>. The Core services insist on
/// an injectable delay so tests can drive time deterministically; in the app this adapter lets the
/// supervision / polling / watchdog loops actually sleep on real wall-clock time.
/// </summary>
internal sealed class TaskDelay : IAsyncDelay
{
    public Task Delay(TimeSpan delay, CancellationToken cancellationToken)
        => Task.Delay(delay, cancellationToken);
}
