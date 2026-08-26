namespace PlcSoftware.Core.Services;

using PlcSoftware.Core.Abstractions;
using PlcSoftware.Core.Models;

/// <summary>
/// Drives a supervised link through its lifecycle state machine:
/// <see cref="ConnectionState.Disconnected"/> → <see cref="ConnectionState.Connecting"/> →
/// <see cref="ConnectionState.Online"/> → <see cref="ConnectionState.HeartbeatLost"/> →
/// <see cref="ConnectionState.Reconnecting"/> → (back to <see cref="ConnectionState.Online"/>).
///
/// <para>Heartbeat: the link is considered alive while consecutive heartbeat probe failures stay
/// below the configured threshold (default 3). Each failure increments the counter; a successful
/// probe resets it. When the threshold is reached the supervisor transitions to
/// <see cref="ConnectionState.HeartbeatLost"/>, tears the link down, then reconnects with an
/// exponential-ish backoff (default 1/2/5/10/30 s). The backoff caps at the final value and resets to
/// the first value after a successful (re)connect.</para>
///
/// <para>Replay guarantee: this supervisor never enqueues, buffers or re-submits write commands. Its
/// only surface to the connection is <see cref="ISupervisedConnection"/> (connect / disconnect /
/// heartbeat probe), which has no write path, so a host-issued write that was still queued when the
/// link dropped is cancelled by the queue's shutdown on disconnect — it is never replayed by the
/// supervisor. See <see cref="ISupervisedConnection"/>.</para>
///
/// <para>No background-task leaks: <see cref="RunAsync"/> is the single loop. It observes the token at
/// every I/O and wait boundary and exits cleanly on cancellation; there are no fire-and-forget
/// continuations, so awaiting <see cref="RunAsync"/> joins the loop.</para>
/// </summary>
public sealed class ConnectionSupervisor
{
    private readonly ISupervisedConnection _connection;
    private readonly IAsyncDelay _delay;
    private readonly TimeSpan _heartbeatInterval;
    private readonly int _requiredFailures;
    private readonly TimeSpan[] _backoff;

    private int _backoffIndex;
    private ConnectionState _state = ConnectionState.Disconnected;

    /// <summary>
    /// Builds a supervisor. <paramref name="backoff"/> defaults to 1/2/5/10/30 s and is capped at its
    /// final element; <paramref name="heartbeatInterval"/> defaults to 1 s.
    /// </summary>
    public ConnectionSupervisor(
        ISupervisedConnection connection,
        IAsyncDelay delay,
        TimeSpan? heartbeatInterval = null,
        int requiredFailures = 3,
        IEnumerable<TimeSpan>? backoff = null)
    {
        _connection = connection ?? throw new ArgumentNullException(nameof(connection));
        _delay = delay ?? throw new ArgumentNullException(nameof(delay));
        _heartbeatInterval = heartbeatInterval ?? TimeSpan.FromSeconds(1);

        if (requiredFailures < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(requiredFailures), requiredFailures, "must be at least 1.");
        }

        _requiredFailures = requiredFailures;

        _backoff = (backoff ?? DefaultBackoff()).ToArray();
        if (_backoff.Length == 0)
        {
            throw new ArgumentException("backoff schedule must not be empty.", nameof(backoff));
        }
    }

    /// <summary>Current state of the supervised link.</summary>
    public ConnectionState CurrentState => _state;

    /// <summary>Raised on every observed state change (fired only when the state actually changes).</summary>
    public event Action<ConnectionState>? StateChanged;

    /// <summary>
    /// Runs the supervision loop until <paramref name="cancellationToken"/> is cancelled. Awaiting the
    /// returned task joins the loop; it completes cleanly on cancellation.
    /// </summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        _backoffIndex = 0;
        SetState(ConnectionState.Disconnected);

        while (!cancellationToken.IsCancellationRequested)
        {
            SetState(ConnectionState.Connecting);
            var connected = await TryConnectAsync(cancellationToken);

            if (connected)
            {
                SetState(ConnectionState.Online);
                // Backoff resets to the first delay on a successful (re)connect.
                _backoffIndex = 0;

                var heartbeatLost = await MonitorHeartbeatAsync(cancellationToken);
                if (!heartbeatLost)
                {
                    // Exited because of cancellation (shutdown), not heartbeat loss.
                    break;
                }

                SetState(ConnectionState.HeartbeatLost);
                await TryDisconnectAsync(cancellationToken);
            }

            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            // Both a failed connect and a lost heartbeat land here: wait the backoff gap,
            // then loop back to Connecting.
            SetState(ConnectionState.Reconnecting);
            var backedOff = await BackoffDelayAsync(cancellationToken);
            if (!backedOff)
            {
                break;
            }
        }

        SetState(ConnectionState.Disconnected);
    }

    private async Task<bool> TryConnectAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _connection.ConnectAsync(cancellationToken);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private async Task TryDisconnectAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _connection.DisconnectAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception)
        {
        }
    }

    /// <summary>
    /// Probes the link until it is healthy enough to keep going or <c>_requiredFailures</c> consecutive
    /// failures trip the heartbeat threshold.
    /// </summary>
    private async Task<bool> MonitorHeartbeatAsync(CancellationToken cancellationToken)
    {
        var consecutiveFailures = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            bool healthy;
            try
            {
                healthy = await _connection.ProbeAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                // Shutdown, not heartbeat loss.
                return false;
            }
            catch (Exception)
            {
                healthy = false;
            }

            if (healthy)
            {
                consecutiveFailures = 0;
            }
            else
            {
                consecutiveFailures++;
                if (consecutiveFailures >= _requiredFailures)
                {
                    return true;
                }
            }

            try
            {
                await _delay.Delay(_heartbeatInterval, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return false;
            }
        }

        return false;
    }

    private async Task<bool> BackoffDelayAsync(CancellationToken cancellationToken)
    {
        var index = Math.Min(_backoffIndex, _backoff.Length - 1);
        var toWait = _backoff[index];
        _backoffIndex = Math.Min(_backoffIndex + 1, _backoff.Length - 1);

        try
        {
            await _delay.Delay(toWait, cancellationToken);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    private void SetState(ConnectionState state)
    {
        if (_state == state)
        {
            return;
        }

        _state = state;
        StateChanged?.Invoke(state);
    }

    private static IEnumerable<TimeSpan> DefaultBackoff()
    {
        yield return TimeSpan.FromSeconds(1);
        yield return TimeSpan.FromSeconds(2);
        yield return TimeSpan.FromSeconds(5);
        yield return TimeSpan.FromSeconds(10);
        yield return TimeSpan.FromSeconds(30);
    }
}
