using PlcSoftware.Core.Abstractions;
using PlcSoftware.Core.Models;

namespace PlcSoftware.App.Services;

/// <summary>
/// <see cref="ICommandService"/> decorator that observes the outcome of every M110/M111 mask command and
/// records it into the <see cref="SimpleHeldStateService"/>. This is the App-layer source of the 屏蔽
/// status flags: M110/M111 are holding commands with no PLC feedback point in the fast block, so the UI's
/// mask state is driven by the HMI's own held command state, never by a snapshot read.
///
/// <para>Recording rule (per review adjudication). On <see cref="CommandStatus.Success"/> the tracker holds
/// the commanded value (true = 屏蔽, false = 释放/复位). On a Rejected or Unknown outcome the write was not
/// confidently delivered, so the tracker holds <c>false</c> — a conservative "not bypassed", because the
/// held state cannot be confirmed without a feedback point. Every non-mask command passes through untouched
/// and never touches the held state.</para>
/// </summary>
internal sealed class CommandServiceMaskAware : ICommandService
{
    private readonly ICommandService _inner;
    private readonly SimpleHeldStateService _held;

    /// <summary>Wraps <paramref name="inner"/> and records mask outcomes into <paramref name="held"/>.</summary>
    public CommandServiceMaskAware(ICommandService inner, SimpleHeldStateService held)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _held = held ?? throw new ArgumentNullException(nameof(held));
    }

    /// <inheritdoc />
    public async Task<CommandResult> ExecuteAsync(CommandRequest request, CancellationToken cancellationToken)
    {
        if (request is null)
        {
            throw new ArgumentNullException(nameof(request));
        }

        var result = await _inner.ExecuteAsync(request, cancellationToken);

        // Only M110/M111 drive the mask flags. A successful write holds the commanded value; any other
        // outcome (rejected offline, unknown transport failure) is recorded as "not bypassed".
        if (request.Target is CommandTarget.LightCurtainBypass or CommandTarget.DoorBypass)
        {
            _held.Record(request.Target, result.Status == CommandStatus.Success && request.Value);
        }

        return result;
    }

    /// <inheritdoc />
    public Task ReleaseJogCommandsAsync(CancellationToken cancellationToken)
        => _inner.ReleaseJogCommandsAsync(cancellationToken);
}
