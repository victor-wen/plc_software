using System.Windows;
using System.Windows.Threading;
using PlcSoftware.App.ViewModels;

namespace PlcSoftware.App.Views;

/// <summary>
/// Application main window. Hosts the navigation bar, the alarm banner, the page host (into which
/// later tasks navigate their page views) and the global status bar bound to <c>MainViewModel</c>.
/// A small dispatcher timer drives the status-bar clock (design §6.1 当前时间).
///
/// <para>The 总览 nav entry shows the overview page in <see cref="PageHost"/>; the page's data context
/// is the injected <see cref="OverviewViewModel"/>. The other nav buttons are still visual placeholders
/// (their pages belong to later tasks).</para>
/// </summary>
public partial class MainWindow : Window
{
    private readonly DispatcherTimer _clock;
    private readonly OverviewView _overviewView;
    private readonly OverviewViewModel _overviewViewModel;

    public MainWindow(OverviewViewModel overviewViewModel, OverviewView overviewView)
    {
        InitializeComponent();

        _overviewViewModel = overviewViewModel ?? throw new ArgumentNullException(nameof(overviewViewModel));
        _overviewView = overviewView ?? throw new ArgumentNullException(nameof(overviewView));

        _clock = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _clock.Tick += (_, _) => ClockText.Text = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        _clock.Start();
        ClockText.Text = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
    }

    /// <summary>Navigates to the overview page (design §6.2) by hosting the injected view in the page
    /// region and binding it to the overview view model.</summary>
    private void OnOverviewClicked(object sender, RoutedEventArgs e)
    {
        if (_overviewView.DataContext is null)
        {
            _overviewView.DataContext = _overviewViewModel;
        }

        PageHost.Content = _overviewView;
    }
}
