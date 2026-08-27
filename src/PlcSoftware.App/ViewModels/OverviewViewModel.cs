using CommunityToolkit.Mvvm.ComponentModel;
using PlcSoftware.Core.Models;
using PlcSoftware.Core.Services;

namespace PlcSoftware.App.ViewModels;

/// <summary>
/// Maps a decoded <see cref="DeviceSnapshot"/> plus the supervised link state into the read-only
/// overview display (design §6.2): the 6-step flow highlight (等待进板 / 进料 / 挡停定位 / 触发相机 /
/// 请求放行 / 放行) driven by D200 and the single-hot M200-M205 flags, the key safety sensors
/// (M313 安全光栅, M314 前门, M315 后门, M316 气压), the 挡停 (stopper) position (M303 阻挡原位 /
/// M304 阻挡工作位), the current/target width (D203/D202), the belt speed (D205) and the production
/// count (D207.D208 composite).
///
/// <para><b>No UI-thread dependency.</b> The view model consumes Core snapshots and state events through
/// <see cref="ApplySnapshot"/> and <see cref="ApplyConnectionState"/>. It never touches a
/// <c>Dispatcher</c> or any WPF type, so it stays testable under a pure unit test host (the App tests are
/// CI-only on Windows because the WindowsDesktop runtime cannot run on the WSL cross-build, not because
/// this class needs WPF).</para>
///
/// <para><b>Offline behaviour.</b> <see cref="IsOnline"/> is true only when the supervised link reports
/// <see cref="ConnectionState.Online"/>. Every other link state (disconnected / connecting /
/// reconnecting / heartbeat lost) greys out the read-only displays (the XAML knocks the opacity down on
/// <c>IsOnline == false</c>) while <see cref="LastUpdateText"/> keeps showing the frozen snapshot
/// timestamp (design §5.3: 断线时冻结最后快照并显示时间戳).</para>
/// </summary>
public sealed partial class OverviewViewModel : ObservableObject
{
    /// <summary>The six automatic-flow step names (M200-M205 / design §6.2).</summary>
    private static readonly string[] StepNames =
    {
        "等待进板", "进料", "挡停定位", "触发相机", "请求放行", "放行",
    };

    [ObservableProperty]
    private ConnectionState _connectionState;

    [ObservableProperty]
    private DateTime? _lastUpdateTime;

    [ObservableProperty]
    private int _stepNumber;

    /// <summary>The step considered active (single source for the highlight). Null when the snapshot is
    /// ambiguous (no or more than one step flag) so a corrupt step is never highlighted.</summary>
    [ObservableProperty]
    private int? _activeStep;

    [ObservableProperty]
    private bool _lightCurtain;

    [ObservableProperty]
    private bool _frontDoor;

    [ObservableProperty]
    private bool _rearDoor;

    [ObservableProperty]
    private bool _airPressure;

    [ObservableProperty]
    private bool _stopperHome;

    [ObservableProperty]
    private bool _stopperWork;

    [ObservableProperty]
    private int _targetWidth;

    [ObservableProperty]
    private int _currentWidth;

    [ObservableProperty]
    private int _beltSpeed;

    [ObservableProperty]
    private uint _productionCount;

    public OverviewViewModel()
    {
        _connectionState = ConnectionState.Disconnected;
    }

    /// <summary>True only when the supervised link is <see cref="ConnectionState.Online"/>.</summary>
    public bool IsOnline => ConnectionState == ConnectionState.Online;

    /// <summary>Human-readable link text (在线 / 离线 / …) for the page header.</summary>
    public string ConnectionStatusText => ConnectionState switch
    {
        ConnectionState.Online => "在线",
        ConnectionState.Connecting => "连接中",
        ConnectionState.Reconnecting => "重连中",
        ConnectionState.HeartbeatLost => "心跳丢失",
        _ => "离线",
    };

    /// <summary>The formatted last-update timestamp in local time, or 无数据 before the first real snapshot.
    /// A stored <see cref="DateTime.MinValue"/> (the seeded empty-store snapshot timestamp) is treated as
    /// no data so the app start never renders "0001-01-01 00:00:00".</summary>
    public string LastUpdateText =>
        LastUpdateTime is DateTime time && time != DateTime.MinValue
            ? time.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss")
            : "无数据";

    /// <summary>True once at least one snapshot has been applied.</summary>
    public bool HasData => LastUpdateTime.HasValue;

    /// <summary>The name of the active step, or 未知 when no step is resolved.</summary>
    public string StepName => ActiveStep is int step && step >= 0 && step < StepNames.Length
        ? StepNames[step]
        : "未知";

    // Step highlight flags — all derived from the single active step so exactly one can be on.
    public bool IsStep0 => ActiveStep == 0;
    public bool IsStep1 => ActiveStep == 1;
    public bool IsStep2 => ActiveStep == 2;
    public bool IsStep3 => ActiveStep == 3;
    public bool IsStep4 => ActiveStep == 4;
    public bool IsStep5 => ActiveStep == 5;

    /// <summary>安全光栅 X7 text: 遮挡 when the curtain is triggered, else 正常.</summary>
    public string LightCurtainStatus => LightCurtain ? "遮挡" : "正常";

    /// <summary>前门 X16 text.</summary>
    public string FrontDoorStatus => FrontDoor ? "打开" : "关闭";

    /// <summary>后门 X17 text.</summary>
    public string RearDoorStatus => RearDoor ? "打开" : "关闭";

    /// <summary>气压 X22 text: 正常 when pressure is present, else 低.</summary>
    public string AirPressureStatus => AirPressure ? "正常" : "低";

    /// <summary>挡停 position: 工作位 (extended), 原位 (retracted), 异常 (both — illegal), 未知 (neither).</summary>
    public string StopperStatus => StopperWork
        ? StopperHome ? "异常" : "工作位"
        : StopperHome ? "原位" : "未知";

    partial void OnConnectionStateChanged(ConnectionState value)
    {
        OnPropertyChanged(nameof(IsOnline));
        OnPropertyChanged(nameof(ConnectionStatusText));
    }

    partial void OnLastUpdateTimeChanged(DateTime? value)
    {
        OnPropertyChanged(nameof(LastUpdateText));
        OnPropertyChanged(nameof(HasData));
    }

    partial void OnActiveStepChanged(int? value)
    {
        OnPropertyChanged(nameof(StepName));
        OnPropertyChanged(nameof(IsStep0));
        OnPropertyChanged(nameof(IsStep1));
        OnPropertyChanged(nameof(IsStep2));
        OnPropertyChanged(nameof(IsStep3));
        OnPropertyChanged(nameof(IsStep4));
        OnPropertyChanged(nameof(IsStep5));
    }

    partial void OnLightCurtainChanged(bool value) => OnPropertyChanged(nameof(LightCurtainStatus));

    partial void OnFrontDoorChanged(bool value) => OnPropertyChanged(nameof(FrontDoorStatus));

    partial void OnRearDoorChanged(bool value) => OnPropertyChanged(nameof(RearDoorStatus));

    partial void OnAirPressureChanged(bool value) => OnPropertyChanged(nameof(AirPressureStatus));

    partial void OnStopperHomeChanged(bool value) => OnPropertyChanged(nameof(StopperStatus));

    partial void OnStopperWorkChanged(bool value) => OnPropertyChanged(nameof(StopperStatus));

    /// <summary>Applies an observed supervised-link state. Only <see cref="ConnectionState.Online"/>
    /// keeps the read-only displays active.</summary>
    public void ApplyConnectionState(ConnectionState state) => ConnectionState = state;

    /// <summary>
    /// Applies one decoded snapshot, refreshing the step, sensors, widths, belt speed and production
    /// readouts and recording the snapshot timestamp as the last-update time.
    /// </summary>
    public void ApplySnapshot(DeviceSnapshot snapshot)
    {
        if (snapshot is null)
        {
            throw new ArgumentNullException(nameof(snapshot));
        }

        var values = snapshot.Values;

        // A seeded/empty store snapshot carries DateTime.MinValue (DateTime default). That is not a real
        // snapshot yet, so surface 无数据 (LastUpdateTime = null) instead of "0001-01-01 00:00:00".
        LastUpdateTime = snapshot.Timestamp == DateTime.MinValue ? null : snapshot.Timestamp;

        StepNumber = ReadInt(values, "D200") ?? 0;
        ActiveStep = ComputeActiveStep(values, StepNumber);

        // 待联调：传感器位 (M313 光栅 / M314 前门 / M315 后门 / M316 气压) 与 挡停 (M303 原位 / M304 工作位)
        // 的布尔极性是对点表继电器名的解释 (design §4.6)，PLC 镜像极性未在点表明确，需设备到位后联调确认。
        LightCurtain = ReadBool(values, "M313");
        FrontDoor = ReadBool(values, "M314");
        RearDoor = ReadBool(values, "M315");
        AirPressure = ReadBool(values, "M316");

        StopperHome = ReadBool(values, "M303");
        StopperWork = ReadBool(values, "M304");

        TargetWidth = ReadInt(values, "D202") ?? 0;
        CurrentWidth = ReadInt(values, "D203") ?? 0;
        BeltSpeed = ReadInt(values, "D205") ?? 0;
        ProductionCount = ReadUInt(values, RegisterDecoder.ProductionCountKey) ?? 0;
    }

    /// <summary>
    /// Resolves the active step from the single-hot M200-M205 flags. Precedence:
    /// <list type="number">
    /// <item>A clean single live flag wins the highlight — the fast group (D100 block) is polled fresher
    /// than the process group, so when a flag disagrees with D200 the live flag reflects the newest
    /// state (design §6.2).</item>
    /// <item>When no flag is live but D200 names a valid step (0..5), D200 is trusted (the decoder can
    /// emit D200 before the flag map in a partial read).</item>
    /// <item>More than one live flag is a corrupt/racing snapshot, so the result is null rather than
    /// arbitrarily picking one.</item>
    /// </list>
    /// <see cref="OverviewViewModel.StepNumber"/> always carries the raw D200 so a divergence between the
    /// two sources stays visible to the operator.
    /// </summary>
    private static int? ComputeActiveStep(IReadOnlyDictionary<string, object?> values, int stepNumber)
    {
        var flags = new bool[6];
        var liveCount = 0;
        var liveIndex = -1;
        for (var i = 0; i < flags.Length; i++)
        {
            flags[i] = ReadBool(values, $"M{200 + i}");
            if (flags[i])
            {
                liveCount++;
                liveIndex = i;
            }
        }

        if (liveCount == 1)
        {
            return liveIndex;
        }

        return liveCount == 0 && stepNumber is >= 0 and <= 5 ? stepNumber : null;
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

    private static uint? ReadUInt(IReadOnlyDictionary<string, object?> values, string key)
        => values.TryGetValue(key, out var value)
            ? value switch
            {
                uint ui => ui,
                ushort u => u,
                int i => (uint)i,
                short s => (ushort)s,
                byte b => b,
                _ => null,
            }
            : null;
}
