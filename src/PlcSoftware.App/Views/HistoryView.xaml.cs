using System.Windows;
using System.Windows.Controls;

namespace PlcSoftware.App.Views;

/// <summary>
/// 报警与历史 page (design §7). Code-behind only assigns the injected view model as
/// <see cref="FrameworkElement.DataContext"/>; every display / command concern lives in
/// <see cref="ViewModels.HistoryViewModel"/>.
/// </summary>
public partial class HistoryView : UserControl
{
    public HistoryView()
    {
        InitializeComponent();
    }
}
