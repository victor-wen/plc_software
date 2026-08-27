using System.Windows;
using System.Windows.Threading;

namespace PlcSoftware.App.Views;

/// <summary>
/// Application main window. Hosts the navigation bar, the alarm banner, the page host (into which
/// later tasks navigate their page views) and the global status bar bound to <c>MainViewModel</c>.
/// A small dispatcher timer drives the status-bar clock (design §6.1 当前时间).
/// </summary>
public partial class MainWindow : Window
{
    private readonly DispatcherTimer _clock;

    public MainWindow()
    {
        InitializeComponent();

        _clock = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _clock.Tick += (_, _) => ClockText.Text = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        _clock.Start();
        ClockText.Text = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
    }
}
