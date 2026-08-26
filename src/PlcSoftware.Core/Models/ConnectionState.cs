namespace PlcSoftware.Core.Models;

/// <summary>
/// Link state between the host and the PLC.
/// </summary>
public enum ConnectionState
{
    Disconnected,
    Connecting,
    Online,
    HeartbeatLost,
    Reconnecting,
}
