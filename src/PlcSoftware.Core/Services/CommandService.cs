namespace PlcSoftware.Core.Services;

using PlcSoftware.Core.Abstractions;
using PlcSoftware.Core.Models;

/// <summary>
/// Executes the host command surface (design §4.4) over the shared <see cref="IModbusClient"/>, gated by
/// an injectable <see cref="ICommandGate"/> and paced by an injectable <see cref="IAsyncDelay"/> so the
/// ~200 ms pulse never blocks real wall-clock time in tests.
///
/// <para>Pulse (M100-M103): set true → <see cref="PulseWidth"/> → set false. Holding (M104/M105/M110/M111):
/// maintained write of the request value. Jog (M106-M109): set true and return; the caller releases via
/// <see cref="ReleaseJogCommandsAsync"/>.</para>
///
/// <para>Gating. <see cref="ExecuteAsync"/> rejects a command with <see cref="CommandStatus.Rejected"/>
/// before any write when the link is offline (<see cref="ICommandGate.IsOnline"/> false) or — for a jog —
/// when the machine is not manual-idle (<see cref="ICommandGate.IsManualIdle"/> false).
/// <see cref="ReleaseJogCommandsAsync"/> likewise performs <em>no</em> writes while offline (§5.3), but
/// runs best-effort over an Online link using a non-canceled token so an already-canceled app-exit token
/// cannot prevent the release (§6.4: 应用退出时均尝试复位命令).</para>
///
/// <para>Result-unknown (design §5.3). A write that fails (e.g. a response timeout) yields
/// <see cref="CommandStatus.Unknown"/> and the pulse is <em>not</em> repeated and the release write is
/// <em>not</em> scheduled — the PLC-side state cannot be trusted, so no blind retry. A cancellation
/// mid-pulse is the one exception: the coil is best-effort cleared (to avoid latching true) before the
/// <see cref="OperationCanceledException"/> is rethrown.</para>
///
/// <para>Mode exclusivity (M104/M105). The service writes only the requested bit; the mutually exclusive
/// mode combos (§4.4: 手动 M104=0,M105=0 / 自动 M104=1,M105=0 / 直通 M104=0,M105=1) are composed by the UI,
/// which must wait for the PLC's final mode (M1/M2/M13) rather than trusting a single write result.</para>
/// </summary>
public sealed class CommandService : ICommandService
{
    /// <summary>The nominal width of an M100-M103 command pulse (~200 ms, design §4.4).</summary>
    public static readonly TimeSpan PulseWidth = TimeSpan.FromMilliseconds(200);

    private static readonly IReadOnlyDictionary<CommandTarget, CommandSpec> Specs = BuildSpecs();
    private static readonly IReadOnlyList<CommandSpec> JogSpecs = Specs.Values
        .Where(s => s.Kind == CommandKind.Jog)
        .OrderBy(s => s.Address)
        .ToArray();

    private readonly IModbusClient _client;
    private readonly ICommandGate _gate;
    private readonly IAsyncDelay _delay;
    private readonly byte _slaveId;
    private readonly TimeSpan _pulseWidth;

    /// <summary>Builds the service over the shared single-queue client. <c>slaveId</c> defaults to 1 (the point-map target).</summary>
    public CommandService(IModbusClient client, ICommandGate gate, IAsyncDelay delay, byte slaveId = 1)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _gate = gate ?? throw new ArgumentNullException(nameof(gate));
        _delay = delay ?? throw new ArgumentNullException(nameof(delay));
        _slaveId = slaveId;
        _pulseWidth = PulseWidth;
    }

    /// <inheritdoc />
    public async Task<CommandResult> ExecuteAsync(CommandRequest request, CancellationToken cancellationToken)
    {
        if (request is null)
        {
            throw new ArgumentNullException(nameof(request));
        }

        // A target that isn't in the point map (e.g. an invalid enum cast) is a pre-write validation
        // failure: reject rather than throw a KeyNotFoundException to the caller.
        if (!Specs.TryGetValue(request.Target, out var spec))
        {
            return new CommandResult(CommandStatus.Rejected, request.Target, "unknown command target");
        }

        // Gate: offline denies every write; only a manual-idle machine accepts the manual jogs.
        if (!_gate.IsOnline)
        {
            return new CommandResult(CommandStatus.Rejected, request.Target, "link offline");
        }

        if (spec.Kind == CommandKind.Jog && !_gate.IsManualIdle)
        {
            return new CommandResult(CommandStatus.Rejected, request.Target, "machine not manual-idle");
        }

        try
        {
            switch (spec.Kind)
            {
                case CommandKind.Pulse:
                    await WriteCoilAsync(spec.Address, value: true, cancellationToken);
                    await _delay.Delay(_pulseWidth, cancellationToken);
                    await WriteCoilAsync(spec.Address, value: false, cancellationToken);
                    break;

                case CommandKind.Holding:
                    await WriteCoilAsync(spec.Address, request.Value, cancellationToken);
                    break;

                case CommandKind.Jog:
                    await WriteCoilAsync(spec.Address, request.Value, cancellationToken);
                    break;
            }

            return new CommandResult(CommandStatus.Success, request.Target);
        }
        catch (OperationCanceledException)
        {
            // Shutdown may cancel a pulse mid-window, after the set-true edge has landed; the coil could
            // be latched true. Best-effort clear it before surfacing the cancellation so a canceled reset
            // is not left on (design §6.4 release semantics). Best-effort: any failure to clear here is
            // swallowed — the PLC watchdog / UI reconcile handles it.
            if (spec.Kind == CommandKind.Pulse)
            {
                try
                {
                    await WriteCoilAsync(spec.Address, value: false, CancellationToken.None);
                }
                catch
                {
                    // The clear could not be delivered; the coil is left for the watchdog / UI to resolve.
                }
            }

            throw;
        }
        catch (Exception ex)
        {
            // A failure mid-command is "result unknown": do NOT repeat the pulse and do NOT schedule the
            // release write (design §5.3). The UI reconciles by reading PLC state.
            return new CommandResult(CommandStatus.Unknown, request.Target, ex.Message);
        }
    }

    /// <inheritdoc />
    public async Task ReleaseJogCommandsAsync(CancellationToken cancellationToken)
    {
        // §5.3 disables ALL write operations while offline (断线), so a dropped link skips the release.
        // Trade-off: skipping the writes while offline means a jog that was latched online can only be
        // cleared by the D106 watchdog once the link recovers — but writing offline is forbidden by design.
        if (!_gate.IsOnline)
        {
            return;
        }

        // Best-effort release of M106-M109 (design §6.4). App-exit / page-switch tokens arrive already
        // canceled, so the writes MUST NOT observe the caller's token — otherwise no coil would be
        // released (review finding 2). Use CancellationToken.None and swallow each per-coil transport
        // error, continuing to the remaining coils.
        foreach (var spec in JogSpecs)
        {
            try
            {
                await WriteCoilAsync(spec.Address, value: false, CancellationToken.None);
            }
            catch
            {
                // Keep releasing the remaining coils even if one write fails.
            }
        }
    }

    private Task WriteCoilAsync(ushort address, bool value, CancellationToken cancellationToken)
        => _client.WriteSingleCoilAsync(_slaveId, address, value, cancellationToken);

    private static IReadOnlyDictionary<CommandTarget, CommandSpec> BuildSpecs()
        => new Dictionary<CommandTarget, CommandSpec>
        {
            [CommandTarget.EStopRequest] = new(Address: 100, CommandKind.Pulse),
            [CommandTarget.Start] = new(Address: 101, CommandKind.Pulse),
            [CommandTarget.Stop] = new(Address: 102, CommandKind.Pulse),
            [CommandTarget.Reset] = new(Address: 103, CommandKind.Pulse),
            [CommandTarget.AutoMode] = new(Address: 104, CommandKind.Holding),
            [CommandTarget.BypassMode] = new(Address: 105, CommandKind.Holding),
            [CommandTarget.ManualWidthPlus] = new(Address: 106, CommandKind.Jog),
            [CommandTarget.ManualWidthMinus] = new(Address: 107, CommandKind.Jog),
            [CommandTarget.ManualBeltJog] = new(Address: 108, CommandKind.Jog),
            [CommandTarget.ManualStopper] = new(Address: 109, CommandKind.Jog),
            [CommandTarget.LightCurtainBypass] = new(Address: 110, CommandKind.Holding),
            [CommandTarget.DoorBypass] = new(Address: 111, CommandKind.Holding),
        };

    /// <summary>
    /// Resolved address (protocol/coil space, M100 → 100 … M111 → 111) and write kind for a command target.
    /// </summary>
    private readonly record struct CommandSpec(ushort Address, CommandKind Kind);
}
