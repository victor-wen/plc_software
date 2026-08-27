using System.Windows.Controls;

namespace PlcSoftware.App.Views;

/// <summary>
/// Manual page (design §6.4): 调宽正反转 (M106/M107), 皮带点动 (M108) and 挡停 (M109) as press-and-hold jogs.
/// A pure presentation <c>UserControl</c> whose <c>DataContext</c> is the injected
/// <see cref="ViewModels.ManualViewModel"/>; the press/release logic lives in the
/// <see cref="Behaviors.PressAndHoldBehavior"/> attached to each jog button.
/// </summary>
public partial class ManualView : UserControl
{
    public ManualView()
    {
        InitializeComponent();
    }
}
