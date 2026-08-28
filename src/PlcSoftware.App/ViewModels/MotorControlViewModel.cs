using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PlcSoftware.Core.Models;
using PlcSoftware.Core.Services;

namespace PlcSoftware.App.ViewModels;

/// <summary>
/// 电机控制占位页（威纶通深蓝 HMI）：显示电机相关实时参数。
/// 包含 D126 调宽速度 / D122 皮带速度 / D136 脉冲数 / D138 产量 等，只读实时刷新，点击卡片可跳转参数页。
/// WPF-free：通过 <see cref="ApplySnapshot"/> 刷新，通过注入的导航回调执行跳转。
/// </summary>
public sealed partial class MotorControlViewModel : ObservableObject
{
    private Action? _navigateToParameters;

    /// <summary>由外壳（MainWindow / App）设置的跳转回调；也可通过 <see cref="NavigateToParametersRequested"/> 事件订阅。</summary>
    public event Action? NavigateToParametersRequested;

    /// <summary>设置跳转到参数页的导航回调（由 App Composition Root 在窗口创建后调用）。</summary>
    public void SetNavigator(Action? navigate) => _navigateToParameters = navigate;

    // Compatibility alias used by MainWindow HMI shell.
    public void SetNavigateToParameters(Action? navigate) => SetNavigator(navigate);

    [ObservableProperty]
    private ConnectionState _connectionState;

    [ObservableProperty]
    private DateTime? _lastUpdateTime;

    // 实时值（来自快照）
    [ObservableProperty]
    private int _tuningSpeed;          // D126 调宽速度 Hz

    [ObservableProperty]
    private int _beltSpeed;            // D122 皮带速度 Hz

    [ObservableProperty]
    private int _widthPulseCount;      // D136 脉冲数（单字）

    [ObservableProperty]
    private uint _productionCount;     // D138 产量

    [ObservableProperty]
    private int _targetWidth;          // D128 目标宽度

    [ObservableProperty]
    private int _currentWidth;         // D130 当前宽度

    [ObservableProperty]
    private int _tuningDelta;          // D210 调宽差值

    [ObservableProperty]
    private string? _headerFeedback;

    public MotorControlViewModel(Action? navigateToParameters = null)
    {
        _navigateToParameters = navigateToParameters;
        _connectionState = ConnectionState.Disconnected;
    }

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
        LastUpdateTime is DateTime t && t != DateTime.MinValue
            ? t.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss")
            : "无数据";

    partial void OnConnectionStateChanged(ConnectionState value)
    {
        OnPropertyChanged(nameof(IsOnline));
        OnPropertyChanged(nameof(ConnectionStatusText));
    }

    partial void OnLastUpdateTimeChanged(DateTime? value) => OnPropertyChanged(nameof(LastUpdateText));

    public void ApplyConnectionState(ConnectionState state) => ConnectionState = state;

    public void ApplySnapshot(DeviceSnapshot snapshot)
    {
        if (snapshot is null) throw new ArgumentNullException(nameof(snapshot));
        LastUpdateTime = snapshot.Timestamp == DateTime.MinValue ? null : snapshot.Timestamp;
        var v = snapshot.Values;
        TuningSpeed = ReadInt(v, "D126") ?? 0;
        BeltSpeed = ReadInt(v, "D122") ?? 0;
        WidthPulseCount = ReadInt(v, RegisterDecoder.WidthPulseCountKey) ?? ReadInt(v, "D136") ?? 0;
        ProductionCount = ReadUInt(v, RegisterDecoder.ProductionCountKey) ?? 0;
        TargetWidth = ReadInt(v, "D128") ?? 0;
        CurrentWidth = ReadInt(v, "D130") ?? 0;
        TuningDelta = ReadInt(v, "D210") ?? 0;
    }

    [RelayCommand]
    private void GoToParameters()
    {
        try
        {
            if (_navigateToParameters is not null) _navigateToParameters.Invoke();
            else NavigateToParametersRequested?.Invoke();
            HeaderFeedback = "已跳转至参数页";
        }
        catch (Exception ex)
        {
            HeaderFeedback = $"跳转失败：{ex.Message}";
        }
    }

    [RelayCommand]
    private void RefreshHint()
    {
        HeaderFeedback = IsOnline ? "实时刷新中（250/500 ms）" : "离线：显示最后快照";
    }

    private static int? ReadInt(IReadOnlyDictionary<string, object?> values, string key)
        => values.TryGetValue(key, out var value)
            ? value switch
            {
                ushort u => u,
                int i => i,
                uint ui => ui <= int.MaxValue ? (int)ui : int.MaxValue,
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
