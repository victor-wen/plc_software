using System.Windows;
using System.Windows.Controls;

namespace PlcSoftware.App.Views;

/// <summary>
/// 主页面 (Home)：顶部设备示意图、右侧配方、流程区、底部快捷导航。
/// 底部按钮通过 <see cref="MainWindow"/> 的公开导航方法切换页面；占位导航显示提示。
/// </summary>
public partial class HomeView : UserControl
{
    public HomeView()
    {
        InitializeComponent();
    }

    private void OnIoClicked(object sender, RoutedEventArgs e)
    {
        if (Application.Current?.MainWindow is MainWindow w) w.NavigateToIoDiagnostics();
    }

    private void OnAutoConditionClicked(object sender, RoutedEventArgs e)
    {
        if (Application.Current?.MainWindow is MainWindow w) w.NavigateToPlaceholder("自动条件", "自动条件页（占位，待后续实现）");
    }

    private void OnDiagnosticClicked(object sender, RoutedEventArgs e)
    {
        if (Application.Current?.MainWindow is MainWindow w) w.NavigateToDiagnosticTerminal();
    }

    private void OnStationViewClicked(object sender, RoutedEventArgs e)
    {
        if (Application.Current?.MainWindow is MainWindow w) w.NavigateToPlaceholder("工位视图", "工位视图页（占位，待后续实现）");
    }

    private void OnProbeLifeClicked(object sender, RoutedEventArgs e)
    {
        if (Application.Current?.MainWindow is MainWindow w) w.NavigateToPlaceholder("探针寿命", "探针寿命页（占位，待后续实现）");
    }
}
