using System.Windows.Controls;

namespace PlcSoftware.App.Views;

/// <summary>
/// Operation zone (design §6.3): 启动 / 停止 / 复位 / 急停请求 / 自动 / 手动 / 直通. A pure presentation
/// UserControl whose <c>DataContext</c> is the injected <see cref="ViewModels.OperationViewModel"/>
/// (set by the composition root when the page is hosted); it contains no service or logic of its own.
/// </summary>
public partial class OperationBar : UserControl
{
    public OperationBar()
    {
        InitializeComponent();
    }
}
