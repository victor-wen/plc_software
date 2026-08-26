using System.Diagnostics;
using PlcSoftware.Core.Abstractions;
using PlcSoftware.Core.Models;
using PlcSoftware.Core.Services;

namespace PlcSoftware.Core.Tests.Services;

public class ConnectionSupervisorTests
{
    private const int RequiredFailures = 3;
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromMilliseconds(250);

    private static async Task WaitFor(Func<bool> condition, string message, int timeoutMs = 5000)
    {
        var sw = Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.ElapsedMilliseconds > timeoutMs)
            {
                throw new TimeoutException(message);
            }

            await Task.Delay(10);
        }
    }

    /// <summary>
    /// After two consecutive heartbeat failures the link is still <see cref="ConnectionState.Online"/>;
    /// only the third consecutive failure transitions it to
    /// <see cref="ConnectionState.HeartbeatLost"/> and then <see cref="ConnectionState.Reconnecting"/>.
    /// </summary>
    [Fact]
    public async Task ThreeConsecutiveHeartbeatFailures_RequiredBeforeTransitioning()
    {
        var conn = new FakeConnection(connectSucceeds: true) { ProbeResult = false };
        var manual = new ManualDelay();
        var supervisor = CreateSupervisor(conn, manual);
        var transitions = SnapshotTransitions(supervisor);

        using var cts = new CancellationTokenSource();
        var run = supervisor.RunAsync(cts.Token);
        try
        {
            // First probe (failure #1) then block on the heartbeat delay.
            await WaitFor(() => supervisor.CurrentState == ConnectionState.Online && manual.Requests.Count >= 1,
                "supervisor never reached Online after the first probe.");
            Assert.Equal(1, conn.ProbeCalls);
            Assert.DoesNotContain(transitions, t => t is ConnectionState.HeartbeatLost or ConnectionState.Reconnecting);

            // Failure #2: still Online, counter not yet tripped.
            manual.ReleaseOne();
            await WaitFor(() => supervisor.CurrentState == ConnectionState.Online && manual.Requests.Count >= 2,
                "supervisor did not stay Online after the second failure.");
            Assert.Equal(2, conn.ProbeCalls);
            Assert.Equal(ConnectionState.Online, supervisor.CurrentState);
            Assert.DoesNotContain(transitions, t => t is ConnectionState.HeartbeatLost or ConnectionState.Reconnecting);

            // Failure #3: heartbeat lost -> reconnect.
            manual.ReleaseOne();
            await WaitFor(() => supervisor.CurrentState == ConnectionState.Reconnecting && manual.Requests.Count >= 3,
                "supervisor did not enter Reconnecting after the third failure.");
            Assert.Equal(3, conn.ProbeCalls);
            Assert.Contains(transitions, t => t == ConnectionState.HeartbeatLost);
            Assert.Contains(transitions, t => t == ConnectionState.Reconnecting);
        }
        finally
        {
            cts.Cancel();
            await run;
        }
    }

    /// <summary>
    /// A failed probe followed by a successful one resets the counter, so a subsequent failure starts
    /// a fresh window: the supervisor stays Online and never transitions to HeartbeatLost.
    /// </summary>
    [Fact]
    public async Task SuccessfulProbe_AfterFailure_ResetsHeartbeatCounter()
    {
        // probe1 fails, probe2 succeeds (resets), probe3 fails (fresh window, counter=1) -> still Online.
        var conn = new FakeConnection(connectSucceeds: true) { ProbeResult = false };
        conn.EnqueueProbe(false);
        conn.EnqueueProbe(true);
        var manual = new ManualDelay();
        var supervisor = CreateSupervisor(conn, manual);
        var transitions = SnapshotTransitions(supervisor);

        using var cts = new CancellationTokenSource();
        var run = supervisor.RunAsync(cts.Token);
        try
        {
            await WaitFor(() => supervisor.CurrentState == ConnectionState.Online && manual.Requests.Count >= 1,
                "never reached Online.");
            manual.ReleaseOne();   // probe2 succeeds -> reset
            await WaitFor(() => supervisor.CurrentState == ConnectionState.Online && manual.Requests.Count >= 2,
                "probe2 did not run.");
            manual.ReleaseOne();   // probe3 fails -> counter back at 1
            await WaitFor(() => supervisor.CurrentState == ConnectionState.Online && manual.Requests.Count >= 3,
                "probe3 did not run.");

            Assert.Equal(3, conn.ProbeCalls);
            Assert.Equal(ConnectionState.Online, supervisor.CurrentState);
            Assert.DoesNotContain(transitions, t => t == ConnectionState.HeartbeatLost);
        }
        finally
        {
            cts.Cancel();
            await run;
        }
    }

    /// <summary>
    /// Backoff grows 1 / 2 / 5 / 10 / 30 s and stays capped at 30 s for further failures.
    /// </summary>
    [Fact]
    public async Task BackoffSequence_GrowsAndCapsAtThirtySeconds()
    {
        var conn = new FakeConnection(connectSucceeds: false);
        var manual = new ManualDelay();
        var supervisor = CreateSupervisor(conn, manual);

        var expected = new[]
        {
            TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(2),
            TimeSpan.FromSeconds(5),
            TimeSpan.FromSeconds(10),
            TimeSpan.FromSeconds(30),
            TimeSpan.FromSeconds(30),
        };

        using var cts = new CancellationTokenSource();
        var run = supervisor.RunAsync(cts.Token);
        try
        {
            for (var i = 0; i < expected.Length; i++)
            {
                await WaitFor(() => supervisor.CurrentState == ConnectionState.Reconnecting && manual.Requests.Count >= i + 1,
                    $"never requested backoff #{i}.");
                Assert.Equal(expected[i], manual.Requests[i]);
                manual.ReleaseOne();
            }
        }
        finally
        {
            cts.Cancel();
            await run;
        }
    }

    /// <summary>
    /// After a connection is re-established the backoff schedule resets to 1 s: a later reconnect
    /// waits only 1 s again, instead of the grown value.
    /// </summary>
    [Fact]
    public async Task Backoff_ResetsToFirstDelay_AfterSuccessfulReconnect()
    {
        // first connect fails (consumes 1 s backoff); thereafter connects succeed.
        var conn = new FakeConnection(connectSucceeds: true) { ProbeResult = false };
        conn.EnqueueConnect(succeeds: false);
        var manual = new ManualDelay();
        var supervisor = CreateSupervisor(conn, manual);
        var transitions = SnapshotTransitions(supervisor);

        using var cts = new CancellationTokenSource();
        var run = supervisor.RunAsync(cts.Token);
        try
        {
            // 1st connect fails -> Reconnecting, backoff 1 s.
            await WaitFor(() => supervisor.CurrentState == ConnectionState.Reconnecting && manual.Requests.Count >= 1,
                "first failure did not trigger backoff.");
            Assert.Equal(TimeSpan.FromSeconds(1), manual.Requests[0]);
            manual.ReleaseOne();

            // 2nd connect succeeds -> Online, backoff reset; heartbeat fails 3 times -> reconnect.
            await WaitFor(() => supervisor.CurrentState == ConnectionState.Online && manual.Requests.Count >= 2,
                "reconnect did not reach Online.");
            manual.ReleaseOne();
            await WaitFor(() => supervisor.CurrentState == ConnectionState.Online && manual.Requests.Count >= 3,
                "probe after reconnect did not run.");
            manual.ReleaseOne();   // third failure -> heartbeat lost -> Reconnecting, backoff 1 s (reset)
            await WaitFor(() => supervisor.CurrentState == ConnectionState.Reconnecting && manual.Requests.Count >= 4,
                "second reconnect did not request backoff.");

            // Last requested delay is the reset 1 s backoff, not the grown 2 s.
            Assert.Equal(TimeSpan.FromSeconds(1), manual.Requests[^1]);
            Assert.Contains(transitions, t => t == ConnectionState.HeartbeatLost);
        }
        finally
        {
            cts.Cancel();
            await run;
        }
    }

    /// <summary>
    /// Reconnect must never replay queued host write commands. The supervisor's only surface to the
    /// connection is <see cref="ISupervisedConnection"/> (connect / disconnect / heartbeat probe),
    /// which has no write path — so a re-established link never re-issues an earlier write. Host writes
    /// that were still queued when the link dropped are cancelled by the queue's shutdown on disconnect,
    /// never replayed here (see <see cref="ISupervisedConnection"/>).
    /// </summary>
    [Fact]
    public async Task AfterReconnect_DoesNotReplayQueuedWrites()
    {
        var conn = new FakeConnection(connectSucceeds: true) { ProbeResult = false };
        var manual = new ManualDelay();
        var supervisor = CreateSupervisor(conn, manual);

        using var cts = new CancellationTokenSource();
        var run = supervisor.RunAsync(cts.Token);
        try
        {
            // Drive one complete heartbeat-loss -> reconnect cycle.
            await WaitFor(() => supervisor.CurrentState == ConnectionState.Online && manual.Requests.Count >= 1,
                "never reached Online.");
            manual.ReleaseOne();
            await WaitFor(() => supervisor.CurrentState == ConnectionState.Online && manual.Requests.Count >= 2,
                "second probe did not run.");
            manual.ReleaseOne();
            await WaitFor(() => supervisor.CurrentState == ConnectionState.Reconnecting && manual.Requests.Count >= 3,
                "did not enter Reconnecting.");
            manual.ReleaseOne();   // backoff -> reconnect to Online
            await WaitFor(() => supervisor.CurrentState == ConnectionState.Online && manual.Requests.Count >= 4,
                "did not return to Online after reconnect.");

            var calls = conn.Calls;
            Assert.All(calls, c => Assert.True(
                c is "Connect" or "Disconnect" or "Probe",
                $"supervisor issued a non-lifecycle operation: {c}"));
            Assert.DoesNotContain(calls, c => c is "Write" or "Replay" or "Flush");
            Assert.Equal(conn.ProbeCalls, calls.Count(c => c == "Probe"));

            // Reconnect performed exactly one extra logical (re)connect after the loss.
            Assert.Equal(2, calls.Count(c => c == "Connect"));
            Assert.True(calls.Count(c => c == "Disconnect") >= 1, "supervisor never tore down the link.");
        }
        finally
        {
            cts.Cancel();
            await run;
        }
    }

    /// <summary>
    /// Cancelling the supervision token joins the loop and terminates without leaking a background
    /// task, even while blocked on a backoff delay.
    /// </summary>
    [Fact]
    public async Task Cancellation_JoinsLoop_AndExitsCleanly()
    {
        var conn = new FakeConnection(connectSucceeds: false);
        var manual = new ManualDelay();
        var supervisor = CreateSupervisor(conn, manual);

        using var cts = new CancellationTokenSource();
        var run = supervisor.RunAsync(cts.Token);
        await WaitFor(() => supervisor.CurrentState == ConnectionState.Reconnecting && manual.Requests.Count >= 1,
            "never entered Reconnecting.");
        Assert.False(run.IsCompleted);

        cts.Cancel();
        await run;                       // joins the loop without hanging
        Assert.True(run.IsCompleted);
        Assert.Equal(ConnectionState.Disconnected, supervisor.CurrentState);
    }

    private static ConnectionSupervisor CreateSupervisor(ISupervisedConnection connection, IAsyncDelay delay)
        => new(connection, delay, heartbeatInterval: HeartbeatInterval, requiredFailures: RequiredFailures);

    private static List<ConnectionState> SnapshotTransitions(ConnectionSupervisor supervisor)
    {
        var transitions = new List<ConnectionState>();
        supervisor.StateChanged += s =>
        {
            lock (transitions)
            {
                transitions.Add(s);
            }
        };
        return transitions;
    }

    private sealed class ManualDelay : IAsyncDelay
    {
        private readonly object _sync = new();
        private readonly List<TimeSpan> _requests = new();
        private readonly Queue<TaskCompletionSource<bool>> _waiters = new();

        public IReadOnlyList<TimeSpan> Requests
        {
            get
            {
                lock (_sync)
                {
                    return _requests.ToArray();
                }
            }
        }

        public Task Delay(TimeSpan delay, CancellationToken cancellationToken)
        {
            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_sync)
            {
                _requests.Add(delay);
                _waiters.Enqueue(tcs);
            }

            if (cancellationToken.CanBeCanceled)
            {
                cancellationToken.Register(() => tcs.TrySetCanceled(cancellationToken));
            }

            return tcs.Task;
        }

        public void ReleaseOne()
        {
            TaskCompletionSource<bool> tcs;
            lock (_sync)
            {
                if (_waiters.Count == 0)
                {
                    throw new InvalidOperationException("no pending delay to release.");
                }

                tcs = _waiters.Dequeue();
            }

            tcs.TrySetResult(true);
        }
    }

    private sealed class FakeConnection : ISupervisedConnection
    {
        private readonly object _sync = new();
        private readonly List<string> _calls = new();
        private readonly Queue<bool> _connectOutcomes = new();
        private readonly Queue<bool> _probeOutcomes = new();

        /// <summary>Default connect outcome when no override is queued; <c>false</c> makes every connect fail.</summary>
        private readonly bool _connectSucceeds;

        public FakeConnection(bool connectSucceeds)
        {
            _connectSucceeds = connectSucceeds;
        }

        /// <summary>Result of each probe when not overridden by <see cref="EnqueueProbe"/>; true = healthy.</summary>
        public bool ProbeResult { get; set; } = true;

        public IReadOnlyList<string> Calls
        {
            get
            {
                lock (_sync)
                {
                    return _calls.ToArray();
                }
            }
        }

        public int ProbeCalls
        {
            get
            {
                lock (_sync)
                {
                    return _calls.Count(c => c == "Probe");
                }
            }
        }

        public void EnqueueConnect(bool succeeds)
        {
            lock (_sync)
            {
                _connectOutcomes.Enqueue(succeeds);
            }
        }

        public void EnqueueProbe(bool healthy)
        {
            lock (_sync)
            {
                _probeOutcomes.Enqueue(healthy);
            }
        }

        public Task ConnectAsync(CancellationToken cancellationToken)
        {
            lock (_sync)
            {
                _calls.Add("Connect");
            }

            bool succeeds;
            lock (_sync)
            {
                succeeds = _connectOutcomes.Count > 0 ? _connectOutcomes.Dequeue() : _connectSucceeds;
            }

            if (!succeeds)
            {
                throw new InvalidOperationException("connection refused.");
            }

            return Task.CompletedTask;
        }

        public Task DisconnectAsync(CancellationToken cancellationToken)
        {
            lock (_sync)
            {
                _calls.Add("Disconnect");
            }

            return Task.CompletedTask;
        }

        public Task<bool> ProbeAsync(CancellationToken cancellationToken)
        {
            lock (_sync)
            {
                _calls.Add("Probe");
            }

            bool healthy;
            lock (_sync)
            {
                healthy = _probeOutcomes.Count > 0 ? _probeOutcomes.Dequeue() : ProbeResult;
            }

            return Task.FromResult(healthy);
        }
    }
}
