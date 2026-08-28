using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PlcSoftware.Core.Models;

namespace PlcSoftware.App.ViewModels;

/// <summary>
/// 一条操作记录行（威纶通表格占位：日期 / 时间 / 用户 / 端口 / 描述 / 类型）。
/// UI 直接绑定到这些字符串列，时间戳保留用于排序/筛选。
/// </summary>
public sealed record OperationRecordRow(
    DateTime Timestamp,
    string Date,
    string Time,
    string User,
    string Port,
    string Description,
    string Type);

/// <summary>
/// 操作记录页（威纶通深蓝 HMI 占位，设计 §7 历史/审计拆分）。
/// 表格列：日期 / 时间 / 用户 / 端口 / 描述 / 类型。
/// 数据源为注入的 <c>AuditRepository.QueryRange</c> 映射结果；空库时保留占位空表并通过 <see cref="StatusText"/> 提示，
/// 可选在 <see cref="UsePlaceholderWhenEmpty"/> 打开时以模拟行填充以演示表格样式。
/// WPF-free：仅通过 <see cref="QueryCommand"/> 触发加载，状态通过 <see cref="ApplyConnectionState"/> 刷新。
/// </summary>
public sealed partial class OperationRecordViewModel : ObservableObject
{
    private readonly Func<DateTime?, DateTime?, List<OperationRecordRow>> _queryAudits;

    [ObservableProperty]
    private DateTime? _dateFrom;

    [ObservableProperty]
    private DateTime? _dateTo;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string _statusText = string.Empty;

    [ObservableProperty]
    private ConnectionState _connectionState;

    /// <summary>空结果时是否用模拟行占位演示（默认 true，便于无历史库时预览表格）。</summary>
    public bool UsePlaceholderWhenEmpty { get; set; } = true;

    public ObservableCollection<OperationRecordRow> Rows { get; } = new();

    public OperationRecordViewModel(Func<DateTime?, DateTime?, List<OperationRecordRow>>? queryAudits = null)
    {
        _queryAudits = queryAudits ?? ((_, _) => new List<OperationRecordRow>());
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

    public string LastQuerySummary => $"共 {Rows.Count} 条记录";

    partial void OnConnectionStateChanged(ConnectionState value)
    {
        OnPropertyChanged(nameof(IsOnline));
        OnPropertyChanged(nameof(ConnectionStatusText));
    }

    public void ApplyConnectionState(ConnectionState state) => ConnectionState = state;

    [RelayCommand]
    private void Query()
    {
        if (IsBusy) return;
        IsBusy = true;
        try
        {
            var result = _queryAudits(DateFrom, DateTo);
            Rows.Clear();
            foreach (var row in result)
                Rows.Add(row);

            if (Rows.Count == 0 && UsePlaceholderWhenEmpty)
            {
                foreach (var ph in BuildPlaceholderRows())
                    Rows.Add(ph);
                StatusText = result.Count == 0
                    ? $"暂无操作记录，已显示占位示例 {Rows.Count} 条（{FormatRange(DateFrom, DateTo)}）。"
                    : $"查询完成：{Rows.Count} 条（占位）";
            }
            else
            {
                StatusText = $"查询完成：{Rows.Count} 条（{FormatRange(DateFrom, DateTo)}）";
            }

            OnPropertyChanged(nameof(LastQuerySummary));
        }
        catch (Exception ex)
        {
            StatusText = $"查询失败：{ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private static List<OperationRecordRow> BuildPlaceholderRows()
    {
        var now = DateTime.Now;
        // 模拟 6 行，覆盖常见类型：参数 / 屏蔽 / 调试 / 登录 / 启停
        var samples = new (string User, string Port, string Desc, string Type)[]
        {
            ("admin", "D126", "调宽速度 写入 800 Hz", "参数"),
            ("admin", "M110", "光栅屏蔽 保持写入 True", "屏蔽"),
            ("tech", "FC06", "调试终端 写入 D204 = 1200", "调试"),
            ("admin", "M101", "启动 脉冲", "操作"),
            ("admin", "M102", "停止 脉冲", "操作"),
            ("admin", "Login", "登录 成功", "系统"),
        };
        var list = new List<OperationRecordRow>();
        for (var i = 0; i < samples.Length; i++)
        {
            var ts = now.AddMinutes(-i * 13 - 5);
            var s = samples[i];
            list.Add(new OperationRecordRow(
                Timestamp: ts,
                Date: ts.ToString("yyyy-MM-dd"),
                Time: ts.ToString("HH:mm:ss"),
                User: s.User,
                Port: s.Port,
                Description: s.Desc,
                Type: s.Type));
        }
        return list;
    }

    private static string FormatRange(DateTime? from, DateTime? to)
    {
        var f = from?.ToString("yyyy-MM-dd") ?? "—";
        var t = to?.ToString("yyyy-MM-dd") ?? "—";
        return $"{f} 至 {t}";
    }

    /// <summary>将 AuditRepository 原始行映射为表格行（供 App.xaml.cs 布线使用）。</summary>
    public static OperationRecordRow MapAuditRow(Dictionary<string, object?> raw)
    {
        var recordedAtRaw = raw.TryGetValue("recorded_at", out var ra) ? ra as string : null;
        var ts = DateTime.TryParse(recordedAtRaw, null, System.Globalization.DateTimeStyles.RoundtripKind, out var parsed)
            ? parsed
            : DateTime.Now;
        var category = raw.TryGetValue("category", out var c) ? c?.ToString() : null;
        var target = raw.TryGetValue("target", out var t) ? t?.ToString() ?? "—" : "—";
        var valueText = raw.TryGetValue("value_text", out var vt) ? vt?.ToString() : null;
        var messageText = raw.TryGetValue("message_text", out var mt) ? mt?.ToString() : null;

        var typeText = category switch
        {
            "0" => "屏蔽",
            "1" => "参数",
            "2" => "调试",
            _ => category ?? "操作",
        };
        // 兼容数值型 category（AuditCategory 枚举 int）
        if (int.TryParse(category, out var catInt))
        {
            typeText = catInt switch
            {
                0 => "屏蔽",
                1 => "参数",
                2 => "调试",
                _ => $"类型{catInt}",
            };
        }

        var desc = !string.IsNullOrWhiteSpace(messageText) ? messageText!
            : !string.IsNullOrWhiteSpace(valueText) ? $"{target} = {valueText}"
            : target;

        // User/Port 拆分：审计库未存用户时占位显示 admin，Port 用 target
        return new OperationRecordRow(
            Timestamp: ts,
            Date: ts.ToString("yyyy-MM-dd"),
            Time: ts.ToString("HH:mm:ss"),
            User: "admin",
            Port: target,
            Description: desc,
            Type: typeText);
    }
}
