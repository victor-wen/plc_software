using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PlcSoftware.Core.Models;

namespace PlcSoftware.App.ViewModels;

/// <summary>
/// 一条报警总览行（威纶通表格占位：日期 / 时间 / 文本）。
/// 文本格式按需求示例： "三级警告: DB400."400 Alarm".Alarm3[4] 离线模式"
/// </summary>
public sealed record AlarmOverviewRow(
    DateTime Timestamp,
    string Date,
    string Time,
    string Text,
    int Level,       // 1/2/3 级
    string Code);    // 关联故障码/地址

/// <summary>
/// 报警总览页（威纶通深蓝 HMI 占位）。
/// 表格列：日期 / 时间 / 文本。文本为按图片格式的模拟三级警告：离线模式 / 扫码枪屏蔽 / 安全门屏蔽 / 光栅屏蔽。
/// 数据源为注入的 <c>AlarmRepository.QueryOpened</c> 映射结果 + 可配置模拟文本；空库时保留占位空表
/// （<see cref="UseSimulatedTextWhenEmpty"/> 控制是否填充模拟文本以演示黄色警告条样式）。
/// WPF-free：仅通过 <see cref="QueryCommand"/> 触发加载，状态通过 <see cref="ApplyConnectionState"/> 刷新。
/// </summary>
public sealed partial class AlarmOverviewViewModel : ObservableObject
{
    /// <summary>可配置的模拟文本候选（可由外部在构造后替换/追加）。</summary>
    public List<string> SimulatedTexts { get; } = new()
    {
        "离线模式",
        "扫码枪屏蔽",
        "安全门屏蔽",
        "光栅屏蔽",
    };

    private readonly Func<DateTime?, DateTime?, List<AlarmOverviewRow>> _queryAlarms;

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

    /// <summary>空结果时是否用模拟三级警告文本占位（默认 true）。</summary>
    public bool UseSimulatedTextWhenEmpty { get; set; } = true;

    /// <summary>顶部的黄色警告条文本（最新一条的 Text，空则显示“无活动报警”）。</summary>
    [ObservableProperty]
    private string _bannerText = "无活动报警";

    public ObservableCollection<AlarmOverviewRow> Rows { get; } = new();

    public AlarmOverviewViewModel(Func<DateTime?, DateTime?, List<AlarmOverviewRow>>? queryAlarms = null)
    {
        _queryAlarms = queryAlarms ?? ((_, _) => new List<AlarmOverviewRow>());
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

    public bool HasRows => Rows.Count > 0;

    public string LastQuerySummary => $"共 {Rows.Count} 条报警";

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
            var result = _queryAlarms(DateFrom, DateTo);
            Rows.Clear();
            foreach (var row in result)
                Rows.Add(row);

            if (Rows.Count == 0 && UseSimulatedTextWhenEmpty)
            {
                foreach (var ph in BuildSimulatedRows())
                    Rows.Add(ph);
                StatusText = result.Count == 0
                    ? $"暂无真实报警，已显示模拟三级警告 {Rows.Count} 条（{FormatRange(DateFrom, DateTo)}）。"
                    : $"查询完成：{Rows.Count} 条（含模拟）";
            }
            else
            {
                StatusText = $"查询完成：{Rows.Count} 条（{FormatRange(DateFrom, DateTo)}）";
            }

            RefreshBanner();
            OnPropertyChanged(nameof(HasRows));
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

    private void RefreshBanner()
    {
        BannerText = Rows.Count > 0 ? Rows[0].Text : "无活动报警";
    }

    private List<AlarmOverviewRow> BuildSimulatedRows()
    {
        var now = DateTime.Now;
        var list = new List<AlarmOverviewRow>();
        // 地址循环：Alarm3[4..7] 对应不同屏蔽/模式，便于按图片格式展示
        for (var i = 0; i < SimulatedTexts.Count; i++)
        {
            var ts = now.AddMinutes(-i * 7 - 2);
            var addrIndex = 4 + i;
            var text = $"三级警告: DB400.\"400 Alarm\".Alarm3[{addrIndex}] {SimulatedTexts[i]}";
            list.Add(new AlarmOverviewRow(
                Timestamp: ts,
                Date: ts.ToString("yyyy-MM-dd"),
                Time: ts.ToString("HH:mm:ss"),
                Text: text,
                Level: 3,
                Code: $"Alarm3[{addrIndex}]"));
        }
        // 额外补充一条一级/二级示例以演示分级颜色
        var extra = now.AddMinutes(-40);
        list.Add(new AlarmOverviewRow(extra, extra.ToString("yyyy-MM-dd"), extra.ToString("HH:mm:ss"),
            $"二级警告: DB400.\"400 Alarm\".Alarm2[1] 气压低", 2, "K4"));
        var extra2 = now.AddMinutes(-50);
        list.Add(new AlarmOverviewRow(extra2, extra2.ToString("yyyy-MM-dd"), extra2.ToString("HH:mm:ss"),
            $"一级警告: DB400.\"400 Alarm\".Alarm1[0] 急停", 1, "K1"));
        return list;
    }

    private static string FormatRange(DateTime? from, DateTime? to)
    {
        var f = from?.ToString("yyyy-MM-dd") ?? "—";
        var t = to?.ToString("yyyy-MM-dd") ?? "—";
        return $"{f} 至 {t}";
    }

    /// <summary>将 AlarmRepository 原始行映射为总览行（供 App.xaml.cs 布线使用）。</summary>
    public static AlarmOverviewRow MapAlarmRow(Dictionary<string, object?> raw, IReadOnlyList<string>? simulatedTexts = null)
    {
        var openedAtRaw = raw.TryGetValue("opened_at", out var oa) ? oa as string : null;
        var ts = DateTime.TryParse(openedAtRaw, null, System.Globalization.DateTimeStyles.RoundtripKind, out var parsed)
            ? parsed
            : DateTime.Now;
        var code = raw.TryGetValue("code", out var c) ? c?.ToString() ?? "—" : "—";
        var message = raw.TryGetValue("message", out var m) ? m?.ToString() ?? "" : "";
        // 若 message 已是完整文本则直接用，否则按三级警告格式包裹
        string text;
        if (!string.IsNullOrWhiteSpace(message) && message.Contains("警告"))
            text = message;
        else if (!string.IsNullOrWhiteSpace(message))
            text = $"三级警告: DB400.\"400 Alarm\".Alarm3[{code}] {message}";
        else
        {
            // 兜底使用模拟池
            var pool = simulatedTexts ?? new[] { "离线模式", "扫码枪屏蔽", "安全门屏蔽", "光栅屏蔽" };
            var pick = pool[Math.Abs(code.GetHashCode()) % pool.Count];
            text = $"三级警告: DB400.\"400 Alarm\".Alarm3[{code}] {pick}";
        }

        return new AlarmOverviewRow(
            Timestamp: ts,
            Date: ts.ToString("yyyy-MM-dd"),
            Time: ts.ToString("HH:mm:ss"),
            Text: text,
            Level: 3,
            Code: code);
    }
}
