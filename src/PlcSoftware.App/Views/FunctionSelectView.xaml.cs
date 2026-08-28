using System.Windows.Controls;

namespace PlcSoftware.App.Views;

/// <summary>
/// 功能选择页：8 开关网格，蓝/灰 ToggleButton 绑定 ViewModel bool，切换时经 ICommandService 写入对应 M 位。
/// </summary>
public partial class FunctionSelectView : UserControl
{
    public FunctionSelectView()
    {
        InitializeComponent();
    }
}
