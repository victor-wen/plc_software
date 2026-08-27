namespace PlcSoftware.Core.Abstractions;

using PlcSoftware.Core.Models;

/// <summary>
/// Executes the host command surface (design §4.4) against the PLC and releases held jog commands.
///
/// <para>Semantics by target: M100-M103 are ~200 ms set/clear pulses; M104/M105 (mode) and M110/M111
/// (屏蔽) are maintained writes; M106-M109 are press-and-hold jogs released by
/// <see cref="ReleaseJogCommandsAsync"/>.</para>
/// </summary>
public interface ICommandService
{
    /// <summary>
    /// Executes one command. For a jog the coil is set true and the method returns (the caller keeps the
    /// "press"); for a pulse it sets, waits ~200 ms and clears.
    /// </summary>
    Task<CommandResult> ExecuteAsync(CommandRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// Releases every jog coil (M106-M109) to <c>false</c>. Called on mouse-up, page switch, window blur
    /// or app exit (design §6.4). Best-effort: each write is attempted independently and a per-coil
    /// transport error is swallowed, relying on the PLC watchdog for the offline fallback (design §5.2).
    /// </summary>
    Task ReleaseJogCommandsAsync(CancellationToken cancellationToken);
}
