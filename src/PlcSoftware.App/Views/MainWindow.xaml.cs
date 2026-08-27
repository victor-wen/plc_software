using System.Windows;
using System.Windows.Threading;
using PlcSoftware.App.ViewModels;

namespace PlcSoftware.App.Views;

/// <summary>
/// Application main window. Hosts the navigation bar, the alarm banner, the page host (into which
/// later tasks navigate their page views) and the global status bar bound to <c>MainViewModel</c>.
/// A small dispatcher timer drives the status-bar clock (design §6.1 当前时间).
///
/// <para>The 总览 nav entry shows the overview page in <see cref="PageHost"/> and the 操作 entry shows the
/// operation zone (design §6.3); each page's data context is its injected view model. The other nav
/// buttons are still visual placeholders (their pages belong to later tasks).</para>
/// </summary>
public partial class MainWindow : Window
{
    private readonly DispatcherTimer _clock;
    private readonly OverviewView _overviewView;
    private readonly OverviewViewModel _overviewViewModel;
    private readonly OperationBar _operationBar;
    private readonly OperationViewModel _operationViewModel;
    private readonly ManualView _manualView;
    private readonly ManualViewModel _manualViewModel;

    public MainWindow(
        OverviewViewModel overviewViewModel,
        OverviewView overviewView,
        OperationViewModel operationViewModel,
        OperationBar operationBar,
        ManualViewModel manualViewModel,
        ManualView manualView)
    {
        InitializeComponent();

        _overviewViewModel = overviewViewModel ?? throw new ArgumentNullException(nameof(overviewViewModel));
        _overviewView = overviewView ?? throw new ArgumentNullException(nameof(overviewView));
        _operationViewModel = operationViewModel ?? throw new ArgumentNullException(nameof(operationViewModel));
        _operationBar = operationBar ?? throw new ArgumentNullException(nameof(operationBar));
        _manualViewModel = manualViewModel ?? throw new ArgumentNullException(nameof(manualViewModel));
        _manualView = manualView ?? throw new ArgumentNullException(nameof(manualView));

        // On window close (design §6.4 应用退出) best-effort release every jog coil so no manual coil is left
        // latched; the D106 watchdog (§5.2) is the offline fallback.
        Closing += (_, _) => _ = _manualViewModel.ReleaseAllJogsAsync();

        _clock = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _clock.Tick += (_, _) => ClockText.Text = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        _clock.Start();
        ClockText.Text = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
    }

    /// <summary>Navigates to the overview page (design §6.2) by hosting the injected view in the page
    /// region and binding it to the overview view model.</summary>
    private void OnOverviewClicked(object sender, RoutedEventArgs e)
    {
        ReleaseManualJogsOnSwitch();
        if (_overviewView.DataContext is null)
        {
            _overviewView.DataContext = _overviewViewModel;
        }

        PageHost.Content = _overviewView;
    }

    /// <summary>Navigates to the operation zone (design §6.3) by hosting the injected operation bar in the
    /// page region and binding it to the operation view model.</summary>
    private void OnOperationClicked(object sender, RoutedEventArgs e)
    {
        ReleaseManualJogsOnSwitch();
        if (_operationBar.DataContext is null)
        {
            _operationBar.DataContext = _operationViewModel;
        }

        PageHost.Content = _operationBar;
    }

    /// <summary>Navigates to the manual page (design §6.4) by hosting the injected manual view in the page
    /// region and binding it to the manual view model.</summary>
    private void OnManualClicked(object sender, RoutedEventArgs e)
    {
        ReleaseManualJogsOnSwitch();
        if (_manualView.DataContext is null)
        {
            _manualView.DataContext = _manualViewModel;
        }

        PageHost.Content = _manualView;
    }

    /// <summary>Releases every manual jog coil when navigating away from the manual page so a press-and-hold
    /// jog is not left latched by the page switch (design §6.4 切页). Best-effort; the D106 watchdog (§5.2)
    /// is the offline fallback.</summary>
    private void ReleaseManualJogsOnSwitch() => _ = _manualViewModel.ReleaseAllJogsAsync();
}
