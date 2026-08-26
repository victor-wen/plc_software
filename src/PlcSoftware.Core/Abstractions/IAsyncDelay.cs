namespace PlcSoftware.Core.Abstractions;

/// <summary>
/// Injectable asynchronous wait. Production implementations sleep on a timer; tests inject a fake
/// that advances deterministically, so a supervised connection never blocks on real wall-clock time.
/// </summary>
public interface IAsyncDelay
{
    /// <summary>
    /// Waits for <paramref name="delay"/> or until <paramref name="cancellationToken"/> is cancelled.
    /// Cancellation must surface as <see cref="OperationCanceledException"/>.
    /// </summary>
    Task Delay(TimeSpan delay, CancellationToken cancellationToken);
}
