using System.ComponentModel;
using System.Windows;
using System.Windows.Threading;
using PlcSoftware.App.ViewModels;

namespace PlcSoftware.App.Views;

/// <summary>
/// Application main window. Hosts the navigation bar, the alarm banner, the page host (into which
/// later tasks navigate their page views) and the global status bar bound to <c>MainViewModel</c>.
/// A small dispatcher timer drives the status-bar clock (design §6.1 当前时间).
///
/// <para>The 总览 nav entry shows the overview page in <see cref="PageHost"/>, the 操作 entry shows the
/// operation zone (design §6.3), the 手动 entry shows the manual page (design §6.4), the 参数 entry shows the
/// parameter page (design §6.5), the I/O 诊断 entry shows the read-only I/O table (design §6.6) and the 通信设置
/// entry shows the communication-settings page (design §6.8); each page's data context is its injected view
/// model. The remaining nav button (报警与历史) is still a visual placeholder — its page belongs to a later task.</para>
///
/// <para><b>App-exit jog release is best-effort (design §6.4 应用退出).</b> On <see cref="Window.Closing"/> the
/// manual jogs are released through a bounded await (<see cref="OnWindowClosing"/>) so the M106-M109 false
/// writes get a short window to land before <c>App.OnExit</c> stops the host synchronously. The release is
/// <em>not</em> guaranteed to finish exactly once — if it cannot complete (e.g. a stalled transport), the
/// D106 watchdog (design §5.2) is the designated fallback for a latched coil. This is by design, not a bug.</para>
/// </summary>
public partial class MainWindow : Window
{
    /// <summary>The grace window given to the exit-time jog release before shutdown is allowed to proceed.
    /// App.OnExit stops the hosted runtime synchronously immediately after the window closes, so the release
    /// writes are awaited for at most this long (design §6.4 应用退出). If they cannot land in time, the
    /// D106 watchdog (design §5.2) is the designated offline fallback for a latched coil.</summary>
    private static readonly TimeSpan ExitJogReleaseGrace = TimeSpan.FromMilliseconds(500);

    private readonly DispatcherTimer _clock;
    private readonly OverviewView _overviewView;
    private readonly OverviewViewModel _overviewViewModel;
    private readonly OperationBar _operationBar;
    private readonly OperationViewModel _operationViewModel;
    private readonly ManualView _manualView;
    private readonly ManualViewModel _manualViewModel;
    private readonly ParametersView _parametersView;
    private readonly ParametersViewModel _parametersViewModel;
    private readonly IoDiagnosticsView _ioDiagnosticsView;
    private readonly IoDiagnosticsViewModel _ioDiagnosticsViewModel;
    private readonly ConnectionSettingsView _connectionSettingsView;
    private readonly ConnectionSettingsViewModel _connectionSettingsViewModel;
    private readonly HistoryView _historyView;
    private readonly HistoryViewModel _historyViewModel;

    /// <summary>The window-close jog-release task, awaited (bounded) in <see cref="OnWindowClosing"/> so the
    /// M106-M109 false write gets a chance to land before the host stops in <c>App.OnExit</c>.</summary>
    private Task? _exitJogRelease;

    public MainWindow(
        OverviewViewModel overviewViewModel,
        OverviewView overviewView,
        OperationViewModel operationViewModel,
        OperationBar operationBar,
        ManualViewModel manualViewModel,
        ManualView manualView,
        ParametersViewModel parametersViewModel,
        ParametersView parametersView,
        IoDiagnosticsViewModel ioDiagnosticsViewModel,
        IoDiagnosticsView ioDiagnosticsView,
        ConnectionSettingsViewModel connectionSettingsViewModel,
        ConnectionSettingsView connectionSettingsView,
        HistoryViewModel historyViewModel,
        HistoryView historyView)
    {
        InitializeComponent();

        _overviewViewModel = overviewViewModel ?? throw new ArgumentNullException(nameof(overviewViewModel));
        _overviewView = overviewView ?? throw new ArgumentNullException(nameof(overviewView));
        _operationViewModel = operationViewModel ?? throw new ArgumentNullException(nameof(operationViewModel));
        _operationBar = operationBar ?? throw new ArgumentNullException(nameof(operationBar));
        _manualViewModel = manualViewModel ?? throw new ArgumentNullException(nameof(manualViewModel));
        _manualView = manualView ?? throw new ArgumentNullException(nameof(manualView));
        _parametersViewModel = parametersViewModel ?? throw new ArgumentNullException(nameof(parametersViewModel));
        _parametersView = parametersView ?? throw new ArgumentNullException(nameof(parametersView));
        _ioDiagnosticsViewModel = ioDiagnosticsViewModel ?? throw new ArgumentNullException(nameof(ioDiagnosticsViewModel));
        _ioDiagnosticsView = ioDiagnosticsView ?? throw new ArgumentNullException(nameof(ioDiagnosticsView));
        _connectionSettingsViewModel = connectionSettingsViewModel ?? throw new ArgumentNullException(nameof(connectionSettingsViewModel));
        _connectionSettingsView = connectionSettingsView ?? throw new ArgumentNullException(nameof(connectionSettingsView));
        _historyViewModel = historyViewModel ?? throw new ArgumentNullException(nameof(historyViewModel));
        _historyView = historyView ?? throw new ArgumentNullException(nameof(historyView));

        // On window close (design §6.4 应用退出) best-effort release every jog coil so no manual coil is left
        // latched. The release is started here and awaited (bounded) so it can land before App.OnExit stops
        // the host synchronously right after the window closes — but the await is bounded so a stalled release
        // can never hang shutdown. The D106 watchdog (§5.2) is the offline fallback.
        Closing += OnWindowClosing;

        _clock = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _clock.Tick += (_, _) => ClockText.Text = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        _clock.Start();
        ClockText.Text = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
    }

    /// <summary>
    /// Window-close jog release (design §6.4 应用退出). This handler is started on <see cref="Window.Closing"/>
    /// so the M106-M109 false writes can be in flight <em>before</em> <c>App.OnExit</c> stops the host (which
    /// happens synchronously right after the window closes). A <em>fresh</em> release is always started here
    /// (releasing is idempotent) rather than reusing a possibly-stale task, so a jog pressed since any earlier
    /// page-switch release is also released. The awaited task is bounded by <see cref="ExitJogReleaseGrace"/>
    /// and awaited through <see cref="Task.WhenAny"/> so it <em>never</em> blocks the UI thread; if the writes
    /// cannot complete in time, the D106 watchdog (design §5.2) is the designated offline fallback for a
    /// latched coil. Best-effort — app-exit release is not guaranteed to finish exactly once, only to get a
    /// bounded chance.
    /// </summary>
    private async void OnWindowClosing(object? sender, CancelEventArgs e)
    {
        _exitJogRelease = _manualViewModel.ReleaseAllJogsAsync();
        await Task.WhenAny(_exitJogRelease, Task.Delay(ExitJogReleaseGrace));
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

    /// <summary>Navigates to the parameter page (design §6.5) by hosting the injected parameter view in the
    /// page region and binding it to the parameter view model.</summary>
    private void OnParametersClicked(object sender, RoutedEventArgs e)
    {
        ReleaseManualJogsOnSwitch();
        if (_parametersView.DataContext is null)
        {
            _parametersView.DataContext = _parametersViewModel;
        }

        PageHost.Content = _parametersView;
    }

    /// <summary>Navigates to the I/O diagnostics page (design §6.6) by hosting the injected view in the page
    /// region and binding it to the I/O diagnostics view model.</summary>
    private void OnIoDiagnosticsClicked(object sender, RoutedEventArgs e)
    {
        ReleaseManualJogsOnSwitch();
        if (_ioDiagnosticsView.DataContext is null)
        {
            _ioDiagnosticsView.DataContext = _ioDiagnosticsViewModel;
        }

        PageHost.Content = _ioDiagnosticsView;
    }

    /// <summary>Navigates to the communication-settings page (design §6.8) by hosting the injected view in the
    /// page region and binding it to the connection-settings view model.</summary>
    private void OnConnectionSettingsClicked(object sender, RoutedEventArgs e)
    {
        ReleaseManualJogsOnSwitch();
        if (_connectionSettingsView.DataContext is null)
        {
            _connectionSettingsView.DataContext = _connectionSettingsViewModel;
        }

        PageHost.Content = _connectionSettingsView;
    }

    /// <summary>Releases every manual jog coil when navigating away from the manual page so a press-and-hold
    /// jog is not left latched by the page switch (design §6.4 切页). Best-effort; the D106 watchdog (§5.2)
    /// is the offline fallback.</summary>
    private void ReleaseManualJogsOnSwitch() => _ = _manualViewModel.ReleaseAllJogsAsync();

    /// <summary>Navigates to the 报警与历史 page (design §7) by hosting the injected view in the page region
    /// and binding it to the history view model.</summary>
    private void OnHistoryClicked(object sender, RoutedEventArgs e)
    {
        ReleaseManualJogsOnSwitch();
        if (_historyView.DataContext is null)
        {
            _historyView.DataContext = _historyViewModel;
        }

        PageHost.Content = _historyView;
    }
}
