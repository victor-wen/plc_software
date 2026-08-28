using CommunityToolkit.Mvvm.ComponentModel;
using PlcSoftware.Core.Models;

namespace PlcSoftware.App.ViewModels;

/// <summary>
/// 主页面 (Home) ViewModel。复刻 <see cref="OverviewViewModel"/> 模式：WPF-free，仅通过
/// <see cref="ApplySnapshot"/> / <see cref="ApplyConnectionState"/> 消费 Core 状态，不触达 Dispatcher。
/// <para>顶部为设备示意图占位，右侧为配方下拉占位，下方为流程区（D120 步骤映射），底部为状态导航按钮行。</para>
/// </summary>
public sealed partial class HomeViewModel : ObservableObject
{
    private static readonly IReadOnlyDictionary<int, string> AutoStepMap = new Dictionary<int, string>
    {
        [0] = "等待初始化启动",
        [160] = "电机反转",
    };

    private static readonly IReadOnlyDictionary<int, string> OverviewFallbackStepNames = new Dictionary<int, string>
    {
        [0] = "等待进板",
        [1] = "进料",
        [2] = "挡停定位",
        [3] = "触发相机",
        [4] = "请求放行",
        [5] = "放行",
    };

    [ObservableProperty]
    private ConnectionState _connectionState;

    [ObservableProperty]
    private DateTime? _lastUpdateTime;

    [ObservableProperty]
    private int _autoStepNumber;

    [ObservableProperty]
    private string _autoStepText = "等待初始化启动";

    [ObservableProperty]
    private int _initStepNumber;

    [ObservableProperty]
    private string _initStepText = "等待初始化启动";

    [ObservableProperty]
    private string _selectedRecipe = "配方1";

    [ObservableProperty]
    private string _selectedIctModel = "518SII";

    public HomeViewModel()
    {
        _connectionState = ConnectionState.Disconnected;
        _initStepNumber = 0;
        _initStepText = "等待初始化启动";
        _autoStepText = ResolveAutoStepText(0);
    }

    public IReadOnlyList<string> AvailableRecipes { get; } = new[] { "配方1", "配方2" };

    public IReadOnlyList<string> AvailableIctModels { get; } = new[] { "518SII", "518SII-A", "518SII-B" };

    public bool IsOnline => ConnectionState == ConnectionState.Online;

    public string ConnectionStatusText => ConnectionState switch
    {
        ConnectionState.Online => "在线",
        ConnectionState.Connecting => "连接中",
        ConnectionState.Reconnecting => "重连中",
        ConnectionState.HeartbeatLost => "心跳丢失",
        _ => "离线",
    };

    public string LastUpdateText =>
        LastUpdateTime is DateTime time && time != DateTime.MinValue
            ? time.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss")
            : "无数据";

    public bool HasData => LastUpdateTime.HasValue;

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

    partial void OnAutoStepNumberChanged(int value)
    {
        AutoStepText = ResolveAutoStepText(value);
    }

    public void ApplyConnectionState(ConnectionState state) => ConnectionState = state;

    public void ApplySnapshot(DeviceSnapshot snapshot)
    {
        if (snapshot is null)
        {
            throw new ArgumentNullException(nameof(snapshot));
        }

        var values = snapshot.Values;
        LastUpdateTime = snapshot.Timestamp == DateTime.MinValue ? null : snapshot.Timestamp;

        var step = ReadInt(values, "D120") ?? 0;
        AutoStepNumber = step;

        // 初始化流程固定为 Step0: 等待初始化启动（占位），自动流程跟随 D120
        InitStepNumber = 0;
        InitStepText = ResolveAutoStepText(0);
    }

    private static string ResolveAutoStepText(int step)
    {
        if (AutoStepMap.TryGetValue(step, out var mapped))
        {
            return mapped;
        }

        if (OverviewFallbackStepNames.TryGetValue(step, out var fallback))
        {
            return fallback;
        }

        return $"步骤{step}";
    }

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
