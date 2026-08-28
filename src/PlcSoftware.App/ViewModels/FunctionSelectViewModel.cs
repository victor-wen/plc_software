using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PlcSoftware.Core.Abstractions;
using PlcSoftware.Core.Models;

namespace PlcSoftware.App.ViewModels;

/// <summary>
/// 功能选择页 ViewModel。8 个开关：直通模式/离线模式/机台照明/扫码枪屏蔽/蜂鸣器屏蔽/安全门屏蔽/光栅屏蔽/初始化重置。
/// 每个开关用 ToggleButton 样式 (蓝/灰)，绑定到 bool 属性；有地址的 (M105/110/111) 切换时经 <see cref="ICommandService"/> 写入 PLC，其余为 UI 占位。
/// <para>WPF-free：仅通过 <see cref="ApplySnapshot"/> / <see cref="ApplyConnectionState"/> 消费状态，经注入的 <see cref="ICommandService"/> 执行写入，绝不触达 Dispatcher。</para>
/// </summary>
public sealed partial class FunctionSelectViewModel : ObservableObject
{
    private readonly ICommandService _commandService;
    private readonly ICommandGate _gate;
    private bool _suppressWrite;

    [ObservableProperty]
    private ConnectionState _connectionState;

    [ObservableProperty]
    private string _commandFeedbackText = string.Empty;

    [ObservableProperty]
    private bool _isBusy;

    // 8 switches (design: 直通/离线/照明/扫码枪/蜂鸣器/安全门/光栅/初始化)
    [ObservableProperty]
    private bool _isBypassMode; // M105 直通模式

    [ObservableProperty]
    private bool _isOfflineMode; // UI占位 离线模式

    [ObservableProperty]
    private bool _isMachineLightOn; // UI占位 机台照明

    [ObservableProperty]
    private bool _isScannerBypass; // UI占位 扫码枪屏蔽

    [ObservableProperty]
    private bool _isBuzzerBypass; // UI占位 蜂鸣器屏蔽

    [ObservableProperty]
    private bool _isDoorBypass; // M111 门磁屏蔽（安全门屏蔽）

    [ObservableProperty]
    private bool _isLightCurtainBypass; // M110 光栅屏蔽

    [ObservableProperty]
    private bool _isInitReset; // UI占位 初始化重置

    public FunctionSelectViewModel(ICommandService commandService, ICommandGate gate)
    {
        _commandService = commandService ?? throw new ArgumentNullException(nameof(commandService));
        _gate = gate ?? throw new ArgumentNullException(nameof(gate));
        _connectionState = ConnectionState.Disconnected;
    }

    // For test host without gate/commandService: provide parameterless via fake gate
    // But keep primary ctor for DI.

    public bool IsOnline => ConnectionState == ConnectionState.Online;

    public string ConnectionStatusText => ConnectionState switch
    {
        ConnectionState.Online => "在线",
        ConnectionState.Connecting => "连接中",
        ConnectionState.Reconnecting => "重连中",
        ConnectionState.HeartbeatLost => "心跳丢失",
        _ => "离线",
    };

    partial void OnConnectionStateChanged(ConnectionState value)
    {
        OnPropertyChanged(nameof(IsOnline));
        OnPropertyChanged(nameof(ConnectionStatusText));
    }

    // --- Toggle handlers (user-driven) ---
    partial void OnIsBypassModeChanged(bool value)
    {
        if (_suppressWrite) return;
        _ = WriteBypassModeAsync(value);
    }

    partial void OnIsDoorBypassChanged(bool value)
    {
        if (_suppressWrite) return;
        _ = WriteMaskAsync(CommandTarget.DoorBypass, value, "安全门屏蔽");
    }

    partial void OnIsLightCurtainBypassChanged(bool value)
    {
        if (_suppressWrite) return;
        _ = WriteMaskAsync(CommandTarget.LightCurtainBypass, value, "光栅屏蔽");
    }

    partial void OnIsOfflineModeChanged(bool value)
    {
        if (_suppressWrite) return;
        CommandFeedbackText = $"离线模式已{(value ? "开启" : "关闭")}（UI占位，未写入PLC）";
    }

    partial void OnIsMachineLightOnChanged(bool value)
    {
        if (_suppressWrite) return;
        CommandFeedbackText = $"机台照明已{(value ? "开启" : "关闭")}（UI占位，未写入PLC）";
    }

    partial void OnIsScannerBypassChanged(bool value)
    {
        if (_suppressWrite) return;
        CommandFeedbackText = $"扫码枪屏蔽已{(value ? "开启" : "关闭")}（UI占位，未写入PLC）";
    }

    partial void OnIsBuzzerBypassChanged(bool value)
    {
        if (_suppressWrite) return;
        CommandFeedbackText = $"蜂鸣器屏蔽已{(value ? "开启" : "关闭")}（UI占位，未写入PLC）";
    }

    partial void OnIsInitResetChanged(bool value)
    {
        if (_suppressWrite) return;
        CommandFeedbackText = value ? "初始化重置已触发（UI占位，未写入PLC）" : "初始化重置已复位（UI占位）";
    }

    public void ApplyConnectionState(ConnectionState state) => ConnectionState = state;

    public void ApplySnapshot(DeviceSnapshot snapshot)
    {
        if (snapshot is null) throw new ArgumentNullException(nameof(snapshot));
        var values = snapshot.Values;

        // 有地址的开关尝试从快照回读（若快照携带该位）。快照未携带则保持本地状态，不触发写入。
        _suppressWrite = true;
        try
        {
            if (TryReadBool(values, "M105", out var bypass)) IsBypassMode = bypass;
            if (TryReadBool(values, "M111", out var door)) IsDoorBypass = door;
            if (TryReadBool(values, "M110", out var curtain)) IsLightCurtainBypass = curtain;
            // M105 may also appear as CommandTarget address 105 but not decoded; above is best-effort.
        }
        finally
        {
            _suppressWrite = false;
        }
    }

    private async Task WriteBypassModeAsync(bool value)
    {
        if (!_gate.IsOnline)
        {
            CommandFeedbackText = "离线禁止写入：直通模式";
            // revert UI to previous value
            _suppressWrite = true;
            try { IsBypassMode = !value; } finally { _suppressWrite = false; }
            return;
        }

        IsBusy = true;
        try
        {
            var result = await _commandService.ExecuteAsync(new CommandRequest(CommandTarget.BypassMode, value), CancellationToken.None);
            if (result.Status == CommandStatus.Success)
            {
                CommandFeedbackText = $"直通模式已{(value ? "开启" : "关闭")}";
            }
            else
            {
                CommandFeedbackText = $"直通模式{(value ? "开启" : "关闭")}失败：{result.Message ?? result.Status.ToString()}";
                _suppressWrite = true;
                try { IsBypassMode = !value; } finally { _suppressWrite = false; }
            }
        }
        catch (Exception ex)
        {
            CommandFeedbackText = $"直通模式命令失败：{ex.Message}";
            _suppressWrite = true;
            try { IsBypassMode = !value; } finally { _suppressWrite = false; }
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task WriteMaskAsync(CommandTarget target, bool value, string label)
    {
        if (!_gate.IsOnline)
        {
            CommandFeedbackText = $"离线禁止写入：{label}";
            _suppressWrite = true;
            try
            {
                if (target == CommandTarget.DoorBypass) IsDoorBypass = !value;
                else if (target == CommandTarget.LightCurtainBypass) IsLightCurtainBypass = !value;
            }
            finally { _suppressWrite = false; }
            return;
        }

        IsBusy = true;
        try
        {
            var result = await _commandService.ExecuteAsync(new CommandRequest(target, value), CancellationToken.None);
            if (result.Status == CommandStatus.Success)
            {
                CommandFeedbackText = $"{label}已{(value ? "屏蔽" : "释放")}";
            }
            else
            {
                CommandFeedbackText = $"{label}{(value ? "屏蔽" : "释放")}失败：{result.Message ?? result.Status.ToString()}";
                _suppressWrite = true;
                try
                {
                    if (target == CommandTarget.DoorBypass) IsDoorBypass = !value;
                    else if (target == CommandTarget.LightCurtainBypass) IsLightCurtainBypass = !value;
                }
                finally { _suppressWrite = false; }
            }
        }
        catch (Exception ex)
        {
            CommandFeedbackText = $"{label}命令失败：{ex.Message}";
            _suppressWrite = true;
            try
            {
                if (target == CommandTarget.DoorBypass) IsDoorBypass = !value;
                else if (target == CommandTarget.LightCurtainBypass) IsLightCurtainBypass = !value;
            }
            finally { _suppressWrite = false; }
        }
        finally
        {
            IsBusy = false;
        }
    }

    private static bool TryReadBool(IReadOnlyDictionary<string, object?> values, string key, out bool result)
    {
        result = false;
        if (!values.TryGetValue(key, out var value)) return false;
        switch (value)
        {
            case bool b: result = b; return true;
            case ushort u: result = u != 0; return true;
            case int i: result = i != 0; return true;
            case uint ui: result = ui != 0; return true;
            case short s: result = s != 0; return true;
            case byte by: result = by != 0; return true;
            default: return false;
        }
    }
}
