namespace PlcSoftware.Core.Services;

using PlcSoftware.Core.Abstractions;

/// <summary>
/// Drives the D106 host-watchdog counter (上位机看门狗计数, design §5.2: 上位机在线时约每 200 ms 更新 D106;
/// PLC 监视 D106 并在约 500 ms 无变化时清除手动输出).
///
/// <para><b>Online write.</b> Each online cadence step increments the counter by one (UInt16 wraparound:
/// 65535 → 0) and writes it to D106 (protocol address 6) over the shared <see cref="IModbusClient"/>.</para>
///
/// <para><b>断线 (offline).</b> When <see cref="ICommandGate.IsOnline"/> is <c>false</c> the service writes
/// <em>nothing</em> and does <b>not</b> advance the counter (design §5.3: 断线时禁用全部写操作).</para>
///
/// <para><b>Reconnect.</b> On reconnect the counter continues from the <em>current</em> value: the very
/// next online step increments the last-written value by one. It does <b>not</b> reset to 0 and does
/// <b>not</b> replay the values that would have been written during the outage (design §5.3: 重连后只恢复
/// 轮询和 D106，不重放未完成的命令).</para>
///
/// <para><b>Injectable time.</b> The ~200 ms cadence is paced by an injectable <see cref="IAsyncDelay"/>,
/// so tests drive deterministically and the service never blocks on real wall-clock time.</para>
///
/// <para><b>Resilience.</b> <see cref="RunAsync"/> treats a single failed write (non-cancellation) as a
/// transient skip and keeps the loop alive — D106 is the PLC-side offline fallback and must keep running;
/// the transport supervisor owns offline detection. Cancellation joins the loop cleanly.</para>
/// </summary>
public sealed class HmiWatchdogService
{
    /// <summary>The nominal cadence between D106 writes (~200 ms, design §5.2).</summary>
    public static readonly TimeSpan Cadence = TimeSpan.FromMilliseconds(200);

    /// <summary>D106 protocol address (zero-based; register index 6 ↔ D106 in <see cref="RegisterDecoder"/>).</summary>
    public const ushort WatchdogAddress = 6;

    private readonly IModbusClient _client;
    private readonly ICommandGate _gate;
    private readonly IAsyncDelay _delay;
    private readonly byte _slaveId;
    private ushort _lastValue;

    /// <summary>Builds the service over the shared single-queue client. <paramref name="initialValue"/>
    /// seeds the counter so a caller can resume from the last known value (0 by default).</summary>
    public HmiWatchdogService(
        IModbusClient client,
        ICommandGate gate,
        IAsyncDelay delay,
        byte slaveId = 1,
        ushort initialValue = 0)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _gate = gate ?? throw new ArgumentNullException(nameof(gate));
        _delay = delay ?? throw new ArgumentNullException(nameof(delay));
        _slaveId = slaveId;
        _lastValue = initialValue;
    }

    /// <summary>The counter value most recently written to D106 (0 before the first online write).</summary>
    public ushort CurrentValue => _lastValue;

    /// <summary>
    /// Runs the watchdog loop until <paramref name="cancellationToken"/> is cancelled. Each online step
    /// advances and writes D106, then paces the next step by <see cref="Cadence"/>. A single failed write
    /// skips a step rather than tearing the loop down; cancellation joins the loop cleanly.
    /// </summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await AdvanceAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                // Only a genuine shutdown (the loop token cancelled) joins the loop cleanly. An OCE carrying
                // a foreign token — or any transport-level cancellation that is not this loop's shutdown token
                // — is a transient per-step skip (same as a generic write failure), so the D106 fallback keeps
                // ticking rather than being torn down. Fall through to the cadence delay.
                if (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
            }
            catch
            {
                // A transient write failure is a per-step skip — D106 must keep running as the PLC-side
                // fallback; the transport supervisor owns going offline.
            }

            try
            {
                await _delay.Delay(Cadence, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                // Shutdown at a cadence boundary.
                break;
            }
        }
    }

    /// <summary>
    /// One cadence step. When online the counter is incremented (UInt16 wraparound) and written to D106;
    /// when 断线 (<see cref="ICommandGate.IsOnline"/> false) nothing is written and the counter is not
    /// advanced, so a reconnect continues from the current value with no replay.
    /// </summary>
    public async Task AdvanceAsync(CancellationToken cancellationToken)
    {
        // 断线: disable every write and do not advance the counter (design §5.3).
        if (!_gate.IsOnline)
        {
            return;
        }

        // Increment first (not after the write) so the written value is the counter's new state, and the
        // wraparound 65535 → 0 is produced by UInt16 arithmetic (unchecked, as C# default).
        _lastValue = unchecked((ushort)(_lastValue + 1));
        await _client.WriteSingleRegisterAsync(_slaveId, WatchdogAddress, _lastValue, cancellationToken);
    }
}
