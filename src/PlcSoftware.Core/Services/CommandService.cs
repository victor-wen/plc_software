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
/// when the machine is not manual-idle (<see cref="ICommandGate.IsManualIdle"/> false).</para>
///
/// <para>Result-unknown (design §5.3). A write that fails (e.g. a response timeout) yields
/// <see cref="CommandStatus.Unknown"/> and the pulse is <em>not</em> repeated and the release write is
/// <em>not</em> scheduled — the PLC-side state cannot be trusted, so no blind retry.</para>
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

        var spec = Specs[request.Target];

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
            // Shutdown cancels the command; surface it so the caller can join cleanly.
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
        // Best-effort release of M106-M109 (design §6.4). A per-coil transport error is swallowed: the
        // PLC watchdog (D106 heartbeat, §5.2) is the offline fallback that clears the manual outputs.
        foreach (var spec in JogSpecs)
        {
            try
            {
                await WriteCoilAsync(spec.Address, value: false, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
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
