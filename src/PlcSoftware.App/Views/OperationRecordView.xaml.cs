using System.Windows.Controls;

namespace PlcSoftware.App.Views;

/// <summary>
/// 操作记录页（威纶通深蓝 HMI 占位）：表格列 日期/时间/用户/端口/描述/类型，数据源为 AuditsRepository
/// 映射的 AlarmRows 占位展示，样式与报警总览/电机控制共用深蓝主题与黄色警告条。
/// </summary>
public partial class OperationRecordView : UserControl
{
    public OperationRecordView()
    {
        InitializeComponent();
    }
}
