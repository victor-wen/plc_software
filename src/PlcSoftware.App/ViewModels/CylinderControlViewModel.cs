using CommunityToolkit.Mvvm.ComponentModel;
using PlcSoftware.Core.Models;

namespace PlcSoftware.App.ViewModels;

/// <summary>
/// 气缸控制页 ViewModel。表格 5 行：插销气缸/天板气缸/阻挡气缸/轨道气缸/下压气缸.
/// 每行左右各一对 按钮+到位指示（绿灯）。数据来自气缸状态字典（占位数据，可配置），
/// 到位指示绑定 M303/M304/M313 等现有位，其余占位显示"未配置"。
/// WPF-free：仅 ApplySnapshot / ApplyConnectionState，不触达 Dispatcher。
/// </summary>
public sealed partial class CylinderControlViewModel : ObservableObject
{
    public sealed partial class CylinderRow : ObservableObject
    {
        public CylinderRow(string name, string leftAction, string rightAction, string? leftSensorKey, string? rightSensorKey)
        {
            Name = name ?? throw new ArgumentNullException(nameof(name));
            LeftAction = leftAction ?? throw new ArgumentNullException(nameof(leftAction));
            RightAction = rightAction ?? throw new ArgumentNullException(nameof(rightAction));
            LeftSensorKey = leftSensorKey;
            RightSensorKey = rightSensorKey;
        }

        public string Name { get; }
        public string LeftAction { get; }
        public string RightAction { get; }
        public string? LeftSensorKey { get; }
        public string? RightSensorKey { get; }

        [ObservableProperty]
        private bool? _leftSensor;

        [ObservableProperty]
        private bool? _rightSensor;

        public string LeftSensorText => !string.IsNullOrEmpty(LeftSensorKey)
            ? LeftSensor.HasValue ? (LeftSensor.Value ? "到位" : "未到位") : "未配置"
            : "未配置";

        public string RightSensorText => !string.IsNullOrEmpty(RightSensorKey)
            ? RightSensor.HasValue ? (RightSensor.Value ? "到位" : "未到位") : "未配置"
            : "未配置";

        public bool LeftIsConfigured => !string.IsNullOrEmpty(LeftSensorKey);
        public bool RightIsConfigured => !string.IsNullOrEmpty(RightSensorKey);

        partial void OnLeftSensorChanged(bool? value)
        {
            OnPropertyChanged(nameof(LeftSensorText));
        }

        partial void OnRightSensorChanged(bool? value)
        {
            OnPropertyChanged(nameof(RightSensorText));
        }
    }

    [ObservableProperty]
    private ConnectionState _connectionState;

    public CylinderControlViewModel()
    {
        _connectionState = ConnectionState.Disconnected;
        // 初始化 5 行（可配置占位数据，占位数据可通过 CylinderMap 或重新构造定制）
        Cylinders.Add(new CylinderRow("插销气缸", "缩回", "伸出", null, null)); // 占位未配置
        Cylinders.Add(new CylinderRow("天板气缸", "释放", "夹紧", "M313", null)); // 左到位绑定 M313（安全光栅现有位演示），右未配置
        Cylinders.Add(new CylinderRow("阻挡气缸", "缩回", "伸出", "M303", "M304")); // 到位指示绑定 M303/M304 现有位（阻挡原位/工作位）
        Cylinders.Add(new CylinderRow("轨道气缸", "上升", "下降", null, null)); // 占位未配置
        Cylinders.Add(new CylinderRow("下压气缸", "上升", "下降", null, null)); // 占位未配置
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

    /// <summary>
    /// 气缸状态字典（占位数据，可配置）。Key 为气缸名，Value 为行对象；暴露为只读集合供 XAML 绑定。
    /// </summary>
    public List<CylinderRow> Cylinders { get; } = new();

    /// <summary>可选：允许外部以字典形式增删配置（占位数据，可配置）。</summary>
    public IReadOnlyDictionary<string, CylinderRow> CylinderMap => Cylinders.ToDictionary(c => c.Name);

    partial void OnConnectionStateChanged(ConnectionState value)
    {
        OnPropertyChanged(nameof(IsOnline));
        OnPropertyChanged(nameof(ConnectionStatusText));
    }

    public void ApplyConnectionState(ConnectionState state) => ConnectionState = state;

    public void ApplySnapshot(DeviceSnapshot snapshot)
    {
        if (snapshot is null) throw new ArgumentNullException(nameof(snapshot));
        var values = snapshot.Values;
        foreach (var row in Cylinders)
        {
            row.LeftSensor = ReadBoolNullable(values, row.LeftSensorKey);
            row.RightSensor = ReadBoolNullable(values, row.RightSensorKey);
        }
    }

    private static bool? ReadBoolNullable(IReadOnlyDictionary<string, object?> values, string? key)
    {
        if (key is null || !values.TryGetValue(key, out var value)) return null;
        return value switch
        {
            bool b => b,
            ushort u => u != 0,
            int i => i != 0,
            uint ui => ui != 0,
            short s => s != 0,
            byte b => b != 0,
            _ => null,
        };
    }
}
