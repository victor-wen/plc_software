using System.Windows.Controls;

namespace PlcSoftware.App.Views;

/// <summary>
/// 电机控制占位页：显示 D126 调宽速度/D122 皮带速度/D136 脉冲数/D138 产量等实时值，点击卡片可跳转参数页。
/// 威纶通深蓝 HMI 风格，黄色警告条复用，深蓝卡片网格。
/// </summary>
public partial class MotorControlView : UserControl
{
    public MotorControlView()
    {
        InitializeComponent();
    }
}
