using System.Windows.Controls;

namespace PlcSoftware.App.Views;

/// <summary>
/// 报警总览页（威纶通深蓝 HMI 占位）：表格列 日期/时间/文本，文本按 "三级警告: DB400."400 Alarm".Alarm3[4] 离线模式"
/// 格式，数据来自 AlarmRepository + 模拟文本（离线模式/扫码枪屏蔽/安全门屏蔽/光栅屏蔽 三级警告，可配置）。
/// </summary>
public partial class AlarmOverviewView : UserControl
{
    public AlarmOverviewView()
    {
        InitializeComponent();
    }
}
