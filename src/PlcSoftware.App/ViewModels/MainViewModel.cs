using CommunityToolkit.Mvvm.ComponentModel;
using PlcSoftware.Core.Models;
using PlcSoftware.Core.Services;

namespace PlcSoftware.App.ViewModels;

/// <summary>
/// The machine's operating mode as echoed by the PLC (design §4.4: the host commands M104/M105
/// compose the mode, but the <em>final</em> mode the PLC reports is M1/M2/M13). A snapshot that
/// reports none of the mode bits is <see cref="Unknown"/>.
/// </summary>
public enum MachineMode
{
    /// <summary>No mode bit is set in the current snapshot.</summary>
    Unknown,

    /// <summary>M1 手动模式.</summary>
    Manual,

    /// <summary>M2 自动模式.</summary>
    Auto,

    /// <summary>M13 直通模式.</summary>
    Bypass,
}

/// <summary>
/// Maps the link/heartbeat/snapshot state into UI-ready connection-heartbeat-mode-run-fault-mask
/// state for the global status bar and alarm banner (design §6.1).
///
/// <para><b>No UI-thread dependency.</b> The view model consumes Core snapshots and state events
/// through <see cref="ApplySnapshot"/>, <see cref="ApplyConnectionState"/> and
/// <see cref="ApplyHeartbeat"/>. It never touches a <c>Dispatcher</c> or any WPF type, so it stays
/// testable under a pure unit test host (the App tests are CI-only on Windows because the WindowsDesktop
/// runtime cannot run on the WSL cross-build, not because this class needs WPF).</para>
///
/// <para><b>Mode.</b> Derived from the PLC's reported mode bits M1/M2/M13 (manual / auto / bypass),
/// in that precedence order. <b>Run</b> is M3. <b>Fault</b> is the D110 alarm code (0 = no fault),
/// resolved to a message through the injected fault table, or a 故障码 fallback for an unknown code.
/// <b>Mask (屏蔽)</b> is M110 光栅屏蔽 / M111 门磁屏蔽 — but sourced from the HMI's <em>held command</em>
/// state via <see cref="ApplyMaskState"/> (design §4.4), <b>not</b> from a snapshot read: M110/M111 are
/// holding commands with no PLC feedback point in the fast-block register map, so they never appear in a
/// decoded snapshot.</para>
/// </summary>
public sealed partial class MainViewModel : ObservableObject
{
    private readonly IReadOnlyDictionary<int, string> _faultMessages;

    [ObservableProperty]
    private ConnectionState _connectionState;

    [ObservableProperty]
    private HeartbeatStatus _heartbeat;

    [ObservableProperty]
    private MachineMode _mode;

    [ObservableProperty]
    private bool _isRunning;

    [ObservableProperty]
    private int _faultCode;

    [ObservableProperty]
    private string? _faultText;

    [ObservableProperty]
    private bool _hasFault;

    [ObservableProperty]
    private bool _lightCurtainBypass;

    [ObservableProperty]
    private bool _doorBypass;

    /// <summary>
    /// Builds the view model. <paramref name="faultMessages"/> is the loaded K1-K7 fault table
    /// (code → message); an absent code simply leaves <see cref="FaultText"/> null.
    /// </summary>
    public MainViewModel(IReadOnlyDictionary<int, string>? faultMessages = null)
    {
        _faultMessages = faultMessages ?? new Dictionary<int, string>();
        _connectionState = ConnectionState.Disconnected;
        _heartbeat = HeartbeatStatus.Unknown;
        _mode = MachineMode.Unknown;
    }

    /// <summary>Human-readable link text for the status bar (串口状态).</summary>
    public string ConnectionStatusText => ConnectionState switch
    {
        ConnectionState.Online => "在线",
        ConnectionState.Connecting => "连接中",
        ConnectionState.Reconnecting => "重连中",
        ConnectionState.HeartbeatLost => "心跳丢失",
        _ => "离线",
    };

    /// <summary>Human-readable heartbeat text (PLC 心跳).</summary>
    public string HeartbeatText => Heartbeat switch
    {
        HeartbeatStatus.Online => "心跳正常",
        HeartbeatStatus.Lost => "心跳丢失",
        _ => "未知",
    };

    /// <summary>Human-readable mode text (模式).</summary>
    public string ModeText => Mode switch
    {
        MachineMode.Manual => "手动",
        MachineMode.Auto => "自动",
        MachineMode.Bypass => "直通",
        _ => "未知",
    };

    /// <summary>Human-readable run text (运行状态).</summary>
    public string RunText => IsRunning ? "运行" : "停止";

    /// <summary>Human-readable 屏蔽 (bypass) text for the status bar.</summary>
    public string MaskText => LightCurtainBypass || DoorBypass ? "已屏蔽" : "正常";

    partial void OnConnectionStateChanged(ConnectionState value) => OnPropertyChanged(nameof(ConnectionStatusText));

    partial void OnHeartbeatChanged(HeartbeatStatus value) => OnPropertyChanged(nameof(HeartbeatText));

    partial void OnModeChanged(MachineMode value) => OnPropertyChanged(nameof(ModeText));

    partial void OnIsRunningChanged(bool value) => OnPropertyChanged(nameof(RunText));

    partial void OnLightCurtainBypassChanged(bool value) => OnPropertyChanged(nameof(MaskText));

    partial void OnDoorBypassChanged(bool value) => OnPropertyChanged(nameof(MaskText));

    /// <summary>
    /// Applies an observed link state. The status bar shows 串口状态, so this is the link text.
    /// </summary>
    public void ApplyConnectionState(ConnectionState state) => ConnectionState = state;

    /// <summary>Applies the observed PLC heartbeat status.</summary>
    public void ApplyHeartbeat(HeartbeatStatus status) => Heartbeat = status;

    /// <summary>
    /// Applies the HMI's own held mask command state (design §4.4). This is the only source of the 屏蔽
    /// flags — M110/M111 are holding commands, not PLC feedback points, so they are never read from a
    /// snapshot. Called by the composition root when <c>SimpleHeldStateService</c> publishes a change.
    /// </summary>
    public void ApplyMaskState(bool lightCurtainBypass, bool doorBypass)
    {
        LightCurtainBypass = lightCurtainBypass;
        DoorBypass = doorBypass;
    }

    /// <summary>
    /// Applies one decoded snapshot. Reads the mode bits (M1/M2/M13), the run bit (M3) and the fault code
    /// (D110), then refreshes the derived text properties. Mask (M110/M111) is deliberately <em>not</em>
    /// read here — see <see cref="ApplyMaskState"/>.
    /// </summary>
    public void ApplySnapshot(DeviceSnapshot snapshot)
    {
        if (snapshot is null)
        {
            throw new ArgumentNullException(nameof(snapshot));
        }

        var values = snapshot.Values;

        // Mode precedence: auto (M2) > bypass (M13) > manual (M1) > unknown.
        if (ReadBool(values, "M2"))
        {
            Mode = MachineMode.Auto;
        }
        else if (ReadBool(values, "M13"))
        {
            Mode = MachineMode.Bypass;
        }
        else if (ReadBool(values, "M1"))
        {
            Mode = MachineMode.Manual;
        }
        else
        {
            Mode = MachineMode.Unknown;
        }

        IsRunning = ReadBool(values, "M3");

        FaultCode = ReadInt(values, "D110") ?? 0;
        HasFault = FaultCode != 0;
        FaultText = HasFault && _faultMessages.TryGetValue(FaultCode, out var message)
            ? message
            : HasFault ? $"故障码 {FaultCode}" : null;
    }

    private static bool ReadBool(IReadOnlyDictionary<string, object?> values, string key)
        => values.TryGetValue(key, out var value) && value switch
        {
            bool b => b,
            ushort u => u != 0,
            int i => i != 0,
            uint ui => ui != 0,
            _ => false,
        };

    private static int? ReadInt(IReadOnlyDictionary<string, object?> values, string key)
        => values.TryGetValue(key, out var value)
            ? value switch
            {
                ushort u => u,
                int i => i,
                uint ui => (int)ui,
                short s => s,
                byte b => b,
                _ => null,
            }
            : null;
}
