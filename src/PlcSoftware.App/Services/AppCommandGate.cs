using PlcSoftware.Core.Abstractions;
using PlcSoftware.Core.Models;
using PlcSoftware.Core.Services;

namespace PlcSoftware.App.Services;

/// <summary>
/// <see cref="ICommandGate"/> over the live <see cref="ConnectionSupervisor"/> state and the latest
/// <see cref="IDeviceStateStore"/> snapshot. It is the app's read-only view of link + machine state
/// that gates every host write.
///
/// <para><see cref="IsOnline"/> is <c>false</c> whenever the link is not
/// <see cref="ConnectionState.Online"/> (断线: Disconnected / Connecting / HeartbeatLost / Reconnecting).
/// <see cref="IsManualIdle"/> additionally requires Manual mode (M1) and not running (M3).</para>
/// </summary>
internal sealed class AppCommandGate : ICommandGate
{
    private readonly ConnectionSupervisor _supervisor;
    private readonly IDeviceStateStore _store;

    public AppCommandGate(ConnectionSupervisor supervisor, IDeviceStateStore store)
    {
        _supervisor = supervisor ?? throw new ArgumentNullException(nameof(supervisor));
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    public bool IsOnline => _supervisor.CurrentState == ConnectionState.Online;

    public bool IsManualIdle
    {
        get
        {
            if (!IsOnline)
            {
                return false;
            }

            var values = _store.Current.Values;
            return ReadBool(values, "M1") && !ReadBool(values, "M3");
        }
    }

    /// <summary>Reads the machine's run bit (M3) from a decoded snapshot. The diagnostic terminal's
    /// write guard (design §6.5: 机器运行时禁止写入) is wired here, reusing the exact bit the
    /// <see cref="IsManualIdle"/> gate reads.</summary>
    public static bool ReadRunState(IReadOnlyDictionary<string, object?> values)
        => ReadBool(values, "M3");

    private static bool ReadBool(IReadOnlyDictionary<string, object?> values, string key)
        => values.TryGetValue(key, out var value) && value switch
        {
            bool b => b,
            ushort u => u != 0,
            int i => i != 0,
            uint ui => ui != 0,
            _ => false,
        };
}
