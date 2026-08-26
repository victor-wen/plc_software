using System.Threading.Channels;

namespace PlcSoftware.Infrastructure.Modbus;

/// <summary>
/// Serialises asynchronous work so that at most one operation is in flight at any instant, in strict
/// FIFO submission order, behind a single shutdown/cancellation boundary.
///
/// This is the one place every serial-bus request is funneled through, so the underlying transport
/// is never asked to serve two requests at once. <see cref="NModbusRtuClient"/> keeps its own
/// per-call <see cref="SemaphoreSlim"/> as defense in depth; the queue is the primary serialiser.
///
/// <para>Semantics:</para>
/// <list type="bullet">
///   <item><b>FIFO</b> — work executes strictly in the order <see cref="EnqueueAsync{TResult}"/> was
///   called, one at a time.</item>
///   <item><b>Per-request cancellation</b> — each operation runs under a token linked to both the
///   caller's <see cref="CancellationToken"/> and the queue's shutdown token. A caller cancels only
///   its own request (that caller observes <see cref="OperationCanceledException"/>); other requests
///   are unaffected. A request submitted with an already-cancelled token fails immediately without
///   consuming a FIFO position.</item>
///   <item><b>Shutdown</b> — <see cref="ShutdownAsync"/> is a hard stop, not a drain: it aborts the
///   in-flight operation and cancels every queued (pending) operation with
///   <see cref="OperationCanceledException"/>, waits for the worker to exit, and afterwards rejects
///   further submissions with <see cref="ObjectDisposedException"/>. The wait for the worker is
///   <b>bounded</b>: teardown stops waiting once <see cref="DisposeAsync"/>'s internal timeout budget
///   (or the caller-supplied <see cref="CancellationToken"/> to <see cref="ShutdownAsync"/>) elapses,
///   so an operation that ignores cancellation cannot make shutdown hang forever — the queue is still
///   closed and pending items cancelled, and a worker left running an un-cancellable operation is
///   abandoned rather than hung on. <see cref="ShutdownAsync"/> caches its teardown task, so repeated
///   or concurrent calls await the same real completion.</item>
///   <item><b>Skips pre-cancelled items</b> — if a caller cancels after enqueueing but before the
///   worker picks the item up, the worker completes that item as cancelled without invoking its
///   operation.</item>
///   <item><b>Failure isolation</b> — if an operation throws, that exception is delivered to its own
///   caller's task only; the worker keeps processing subsequent queued work.</item>
/// </list>
/// </summary>
public sealed class ModbusRequestQueue : IAsyncDisposable
{
    private readonly Channel<QueueItem> _channel = Channel.CreateUnbounded<QueueItem>();
    private readonly CancellationTokenSource _shutdownCts = new();
    private readonly Task _worker;
    private readonly TimeSpan _shutdownTimeout;
    private readonly object _shutdownLock = new();
    private Task? _shutdownTask;
    private int _shutdown;

    /// <summary>
    /// Creates a queue. <paramref name="shutdownTimeout"/> bounds how long
    /// <see cref="ShutdownAsync"/> / <see cref="DisposeAsync"/> waits for the worker to exit before
    /// giving up (and abandoning an operation that ignores cancellation); it defaults to 5 seconds.
    /// </summary>
    public ModbusRequestQueue(TimeSpan? shutdownTimeout = null)
    {
        _shutdownTimeout = shutdownTimeout ?? TimeSpan.FromSeconds(5);
        _worker = WorkerLoopAsync();
    }

    /// <summary>
    /// Enqueues a void operation. Backed by <see cref="EnqueueAsync{TResult}"/>; returns a task that
    /// completes when the operation does (success, cancellation, or fault).
    /// </summary>
    public Task EnqueueAsync(Func<CancellationToken, Task> operation, CancellationToken cancellationToken)
        => EnqueueAsync<object?>(
            async token =>
            {
                await operation(token);
                return null;
            },
            cancellationToken);

    /// <summary>
    /// Enqueues an operation returning <typeparamref name="TResult"/> and returns a task that
    /// completes when it does. Rejects the submission with <see cref="ObjectDisposedException"/> once
    /// the queue has been shut down.
    /// </summary>
    public Task<TResult> EnqueueAsync<TResult>(Func<CancellationToken, Task<TResult>> operation, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);

        if (Volatile.Read(ref _shutdown) != 0)
        {
            throw new ObjectDisposedException(nameof(ModbusRequestQueue));
        }

        // Fail fast: an already-cancelled caller must not consume a FIFO position.
        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromCanceled<TResult>(cancellationToken);
        }

        var completion = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var item = new QueueItem(
            completion,
            async token => (object?)(await operation(token)),
            cancellationToken);

        if (!_channel.Writer.TryWrite(item))
        {
            // The channel was completed by a concurrent shutdown between the check above and now.
            throw new ObjectDisposedException(nameof(ModbusRequestQueue));
        }

        return UnboxAsync<TResult>(completion.Task);
    }

    /// <summary>
    /// Hard-stops the queue: cancels the in-flight operation, cancels every still-queued operation,
    /// waits for the worker to exit, and rejects all later submissions. The wait for the worker is
    /// bounded by <c>shutdownTimeout</c> (see the constructor) and by <paramref name="cancellationToken"/>,
    /// so it can never hang on an operation that ignores cancellation. Safe to call more than once;
    /// the teardown task is cached, so concurrent or repeated calls await the same real completion.
    /// </summary>
    public Task ShutdownAsync(CancellationToken cancellationToken = default)
    {
        lock (_shutdownLock)
        {
            // Cache the teardown task so a second caller (or a second DisposeAsync) awaits the real
            // completion rather than returning while the worker is still being torn down.
            if (_shutdownTask is null)
            {
                _shutdown = 1;
                _shutdownTask = ShutdownCoreAsync(cancellationToken);
            }

            return _shutdownTask;
        }
    }

    public ValueTask DisposeAsync() => new(ShutdownAsync());

    private async Task ShutdownCoreAsync(CancellationToken cancellationToken)
    {
        // Cancel first so the worker stops reading (its WaitToReadAsync token is shared) and the
        // in-flight operation is aborted; then fail every operation still sitting in the channel.
        _shutdownCts.Cancel();
        _channel.Writer.TryComplete();

        while (_channel.Reader.TryRead(out var pending))
        {
            pending.Completion.TrySetCanceled(_shutdownCts.Token);
        }

        try
        {
            // The worker exits via its shutdown token (or naturally once the channel is drained). It
            // may also be wedged on an operation that ignores cancellation; bound the wait so teardown
            // still completes (queue closed, pending items cancelled above) instead of hanging.
            await _worker.WaitAsync(_shutdownTimeout, cancellationToken);
        }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
        {
            // Budget (or caller cancellation) elapsed before the worker exited. A worker still
            // running an un-cancellable operation is abandoned here; the queue is already closed.
        }
        finally
        {
            _shutdownCts.Dispose();
        }
    }

    private async Task WorkerLoopAsync()
    {
        try
        {
            while (await _channel.Reader.WaitToReadAsync(_shutdownCts.Token))
            {
                while (_channel.Reader.TryRead(out var item))
                {
                    await ExecuteAsync(item);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Shutdown: the in-flight item (if any) is aborted via the shared token and queued items
            // are cancelled by ShutdownAsync, so there is nothing left to do here.
        }
    }

    private async Task ExecuteAsync(QueueItem item)
    {
        // The caller may have cancelled after enqueueing but before the worker picked this item up; in
        // that case complete it as cancelled without invoking the operation (it would never be
        // observed, and must not be executed).
        if (item.CallerToken.IsCancellationRequested)
        {
            item.Completion.TrySetCanceled(item.CallerToken);
            return;
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(_shutdownCts.Token, item.CallerToken);
        try
        {
            var result = await item.Operation(linked.Token).ConfigureAwait(false);
            item.Completion.TrySetResult(result);
        }
        catch (OperationCanceledException ex)
        {
            var token = linked.Token.IsCancellationRequested ? linked.Token : ex.CancellationToken;
            item.Completion.TrySetCanceled(token);
        }
        catch (Exception ex)
        {
            // Deliver the failure to this caller only; the loop keeps serving the next item.
            item.Completion.TrySetException(ex);
        }
    }

    private static async Task<TResult> UnboxAsync<TResult>(Task<object?> boxed)
        => (TResult)(await boxed.ConfigureAwait(false))!;

    private sealed record QueueItem(
        TaskCompletionSource<object?> Completion,
        Func<CancellationToken, Task<object?>> Operation,
        CancellationToken CallerToken);
}
