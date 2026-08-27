namespace PlcSoftware.Core.Models;

/// <summary>
/// The outcome of executing a <see cref="CommandRequest"/>.
/// </summary>
public enum CommandStatus
{
    /// <summary>Every write of the command completed (the request reached the PLC).</summary>
    Success,

    /// <summary>The command was denied before any write was attempted (offline link or
    /// not-manual-idle machine for a jog, design §5.2/§6.4).</summary>
    Rejected,

    /// <summary>
    /// A write failed mid-command (e.g. a write response timeout), so the PLC-side result is
    /// <em>unknown</em>. Per design §5.3 the service must <b>not</b> repeat the pulse or re-issue the
    /// release; it is for the UI / status layer to reconcile by reading state.
    /// </summary>
    Unknown,
}

/// <summary>
/// Immutable result of a command execution. <see cref="Message"/> carries an optional reason, used
/// for <see cref="CommandStatus.Rejected"/> (why the gate denied it) and <see cref="CommandStatus.Unknown"/>
/// (the transport exception text).
/// </summary>
public sealed record CommandResult(CommandStatus Status, CommandTarget Target, string? Message = null);
