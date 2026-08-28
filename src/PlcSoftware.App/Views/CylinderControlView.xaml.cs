using System.Windows.Controls;

namespace PlcSoftware.App.Views;

/// <summary>
/// 气缸控制页：5 行气缸表格，左右按钮+到位绿灯，中间气缸名；到位指示来自 ViewModel 的传感器字典（M303/M304/M313 等现有位，其余显示“未配置”）。
/// </summary>
public partial class CylinderControlView : UserControl
{
    public CylinderControlView()
    {
        InitializeComponent();
    }
}
