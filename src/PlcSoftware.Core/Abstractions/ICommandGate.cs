namespace PlcSoftware.Core.Abstractions;

/// <summary>
/// Read-only view of the link and machine state that gates host writes, so
/// <c>CommandService</c> stays decoupled from the connection supervisor and the snapshot store.
/// A production implementation reads live <see cref="Models.ConnectionState"/> and the latest
/// <see cref="Models.DeviceSnapshot"/> behind these properties.
///
/// <para><b>断线 (offline).</b> <see cref="IsOnline"/> is <c>false</c> when the link is not
/// <see cref="Models.ConnectionState.Online"/> (i.e. Disconnected, Connecting, HeartbeatLost or
/// Reconnecting). Per design §5.2/§5.3 the host must disable <em>all</em> write operations when offline.</para>
///
/// <para><b>非手动运行状态 (not manual-idle).</b> <see cref="IsManualIdle"/> is <c>true</c> only when the
/// machine is in manual mode (<c>M1</c>) and <em>not</em> running (<c>M3</c>). The manual jog commands
/// (M106-M109) are only allowed in this state (design §6.4); the PLC performs the final interlock.</para>
/// </summary>
public interface ICommandGate
{
    /// <summary>True when the link is Online and host writes are permitted (false = 断线).</summary>
    bool IsOnline { get; }

    /// <summary>True when the machine is in manual mode and not running (manual-idle).</summary>
    bool IsManualIdle { get; }
}
