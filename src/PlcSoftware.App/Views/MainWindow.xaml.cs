using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using PlcSoftware.App.Services;
using PlcSoftware.App.ViewModels;
using PlcSoftware.Core.Abstractions;
using PlcSoftware.Core.Configuration;
using PlcSoftware.Core.Models;
using PlcSoftware.Core.Services;

namespace PlcSoftware.App.Views;

/// <summary>
/// HMI 风格主外壳：威纶通深蓝科技风（Header 蓝绿 Logo+OP10 / 黄色警告条 / 左侧垂直导航 / 右侧竖排命令列 / 底部 tab 行 / 中央 PageHost）。
/// 保持现有登录门控逻辑（<see cref="UpdateGateUi"/>, <see cref="IsGateActive"/>）与 <see cref="ConfigurablePage"/> 兼容。
/// 右侧命令列通过 <see cref="ICommandService"/> 执行 M100-M105 等脉冲/保持命令，IsOnline/IsRunning 由 <see cref="MainViewModel"/> 驱动。
/// 底部 tab 全部以占位实现，数据缺失显示“待配置”。
/// 时钟计时器同时驱动 Header 日期时间与黄色警告条时间戳。
/// </summary>
public partial class MainWindow : Window, IConfigurableUiNavigator
{
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
    private readonly DiagnosticTerminalView _diagnosticTerminalView;
    private readonly DiagnosticTerminalViewModel _diagnosticTerminalViewModel;
    private readonly ConnectionSettingsView _connectionSettingsView;
    private readonly ConnectionSettingsViewModel _connectionSettingsViewModel;
    private readonly HistoryView _historyView;
    private readonly HistoryViewModel _historyViewModel;
    private readonly HomeView _homeView;
    private readonly HomeViewModel _homeViewModel;
    private readonly FunctionSelectView _functionSelectView;
    private readonly FunctionSelectViewModel _functionSelectViewModel;
    private readonly CylinderControlView _cylinderControlView;
    private readonly CylinderControlViewModel _cylinderControlViewModel;

    // 新增 HMI 页面（报警总览/操作记录/电机控制）
    private readonly AlarmOverviewView _alarmOverviewView;
    private readonly AlarmOverviewViewModel _alarmOverviewViewModel;
    private readonly OperationRecordView _operationRecordView;
    private readonly OperationRecordViewModel _operationRecordViewModel;
    private readonly MotorControlView _motorControlView;
    private readonly MotorControlViewModel _motorControlViewModel;

    private readonly ICommandService? _configCommandService;
    private readonly ParameterService? _configParameterService;
    private readonly MainViewModel? _configMainViewModel;
    private UiLayoutDefinition? _configLayout;
    private readonly Dictionary<string, ConfigurablePageViewModel> _configPageVms = new();
    private readonly Dictionary<string, ConfigurablePageView> _configPageViews = new();
    private readonly List<string> _configHistory = new();
    private bool _isSignedIn;
    private string? _signedInUser;
    private string? _currentLeftTag;

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
        DiagnosticTerminalViewModel diagnosticTerminalViewModel,
        DiagnosticTerminalView diagnosticTerminalView,
        ConnectionSettingsViewModel connectionSettingsViewModel,
        ConnectionSettingsView connectionSettingsView,
        HistoryViewModel historyViewModel,
        HistoryView historyView,
        HomeViewModel? homeViewModel = null,
        HomeView? homeView = null,
        FunctionSelectViewModel? functionSelectViewModel = null,
        FunctionSelectView? functionSelectView = null,
        CylinderControlViewModel? cylinderControlViewModel = null,
        CylinderControlView? cylinderControlView = null,
        AlarmOverviewViewModel? alarmOverviewViewModel = null,
        AlarmOverviewView? alarmOverviewView = null,
        OperationRecordViewModel? operationRecordViewModel = null,
        OperationRecordView? operationRecordView = null,
        MotorControlViewModel? motorControlViewModel = null,
        MotorControlView? motorControlView = null,
        ICommandService? configCommandService = null,
        ParameterService? configParameterService = null,
        MainViewModel? configMainViewModel = null)
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
        _diagnosticTerminalViewModel = diagnosticTerminalViewModel ?? throw new ArgumentNullException(nameof(diagnosticTerminalViewModel));
        _diagnosticTerminalView = diagnosticTerminalView ?? throw new ArgumentNullException(nameof(diagnosticTerminalView));
        _connectionSettingsViewModel = connectionSettingsViewModel ?? throw new ArgumentNullException(nameof(connectionSettingsViewModel));
        _connectionSettingsView = connectionSettingsView ?? throw new ArgumentNullException(nameof(connectionSettingsView));
        _historyViewModel = historyViewModel ?? throw new ArgumentNullException(nameof(historyViewModel));
        _historyView = historyView ?? throw new ArgumentNullException(nameof(historyView));

        _homeViewModel = homeViewModel ?? new HomeViewModel();
        _homeView = homeView ?? new HomeView { DataContext = _homeViewModel };
        _functionSelectViewModel = functionSelectViewModel ?? CreateDefaultFunctionSelectVm();
        _functionSelectView = functionSelectView ?? new FunctionSelectView { DataContext = _functionSelectViewModel };
        _cylinderControlViewModel = cylinderControlViewModel ?? new CylinderControlViewModel();
        _cylinderControlView = cylinderControlView ?? new CylinderControlView { DataContext = _cylinderControlViewModel };

        _alarmOverviewViewModel = alarmOverviewViewModel ?? new AlarmOverviewViewModel();
        _alarmOverviewView = alarmOverviewView ?? new AlarmOverviewView { DataContext = _alarmOverviewViewModel };
        _operationRecordViewModel = operationRecordViewModel ?? new OperationRecordViewModel();
        _operationRecordView = operationRecordView ?? new OperationRecordView { DataContext = _operationRecordViewModel };
        _motorControlViewModel = motorControlViewModel ?? new MotorControlViewModel();
        _motorControlView = motorControlView ?? new MotorControlView { DataContext = _motorControlViewModel };

        _configCommandService = configCommandService;
        _configParameterService = configParameterService;
        _configMainViewModel = configMainViewModel;

        // 绑定 MotorControl 跳转回调 → 参数页（占位卡片点击跳参数页，受离线/范围保护）
        _motorControlViewModel.SetNavigator(() => NavigateToLeftPage("parameters"));

        if (_configCommandService is not null && _configParameterService is not null)
        {
            SetupConfigurablePages();
        }

        Closing += OnWindowClosing;

        // 时钟：同时驱动 Header 日期时间 + 黄色警告条时间戳
        _clock = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _clock.Tick += (_, _) => TickClocks();
        _clock.Start();
        TickClocks();

        // 初始高亮与占位导航 wiring
        HighlightLeftNav("home");
    }

    private void TickClocks()
    {
        var now = DateTime.Now;
        // Header 右侧：完整日期时间
        try { ClockText.Text = now.ToString("yyyy-MM-dd HH:mm:ss"); } catch { }
        // 黄色警告条：仅时间 08:47:51 风格
        try { WarningClockText.Text = now.ToString("HH:mm:ss"); } catch { }
    }

    private async void OnWindowClosing(object? sender, CancelEventArgs e)
    {
        _exitJogRelease = _manualViewModel.ReleaseAllJogsAsync();
        await Task.WhenAny(_exitJogRelease, Task.Delay(ExitJogReleaseGrace));
    }

    private void OnOverviewClicked(object sender, RoutedEventArgs e) => NavigateToLeftPage("overview");
    private void OnOperationClicked(object sender, RoutedEventArgs e) => NavigateToLeftPage("operation");
    private void OnManualClicked(object sender, RoutedEventArgs e) => NavigateToLeftPage("manual");
    private void OnParametersClicked(object sender, RoutedEventArgs e) => NavigateToLeftPage("parameters");
    private void OnIoDiagnosticsClicked(object sender, RoutedEventArgs e) => NavigateToLeftPage("io-diagnostics");
    private void OnDiagnosticTerminalClicked(object sender, RoutedEventArgs e) => NavigateToLeftPage("diagnostic-terminal");
    private void OnConnectionSettingsClicked(object sender, RoutedEventArgs e) => NavigateToLeftPage("connection-settings");
    private void OnHistoryClicked(object sender, RoutedEventArgs e) => NavigateToLeftPage("history");
    private void OnHomeClicked(object sender, RoutedEventArgs e) => NavigateToLeftPage("home");
    private void OnFunctionSelectClicked(object sender, RoutedEventArgs e) => NavigateToLeftPage("function-select");
    private void OnCylinderControlClicked(object sender, RoutedEventArgs e) => NavigateToLeftPage("cylinder-control");

    private void ReleaseManualJogsOnSwitch() => _ = _manualViewModel.ReleaseAllJogsAsync();

    // --- 左侧垂直导航：主页面/报警总页面/操作记录/气缸控制/电机控制/功能选择 -----------------------
    private void OnLeftNavItemClicked(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string tag && !string.IsNullOrWhiteSpace(tag))
        {
            NavigateToLeftPage(tag);
        }
    }

    private void NavigateToLeftPage(string tag)
    {
        // 登录门控：未登录时任何左侧导航都应被 Navigate 拦截到登录页
        if (IsGateActive && tag != LoginPageId())
        {
            if (_configLayout is not null && LoginPageId() is { } login)
            {
                Navigate(login);
                return;
            }
        }

        ReleaseManualJogsOnSwitch();
        HighlightLeftNav(tag);

        // 优先匹配 HMI 固定页面
        switch (tag)
        {
            case "home":
                if (_homeView.DataContext is null) _homeView.DataContext = _homeViewModel;
                PageHost.Content = _homeView;
                RecordLeftHistory("home");
                return;
            case "alarm-overview":
            case "alarm":
                if (_alarmOverviewView.DataContext is null) _alarmOverviewView.DataContext = _alarmOverviewViewModel;
                PageHost.Content = _alarmOverviewView;
                RecordLeftHistory("alarm-overview");
                return;
            case "operation-record":
                if (_operationRecordView.DataContext is null) _operationRecordView.DataContext = _operationRecordViewModel;
                PageHost.Content = _operationRecordView;
                RecordLeftHistory("operation-record");
                return;
            case "cylinder-control":
                if (_cylinderControlView.DataContext is null) _cylinderControlView.DataContext = _cylinderControlViewModel;
                PageHost.Content = _cylinderControlView;
                RecordLeftHistory("cylinder-control");
                return;
            case "motor-control":
                if (_motorControlView.DataContext is null) _motorControlView.DataContext = _motorControlViewModel;
                PageHost.Content = _motorControlView;
                RecordLeftHistory("motor-control");
                return;
            case "function-select":
                if (_functionSelectView.DataContext is null) _functionSelectView.DataContext = _functionSelectViewModel;
                PageHost.Content = _functionSelectView;
                RecordLeftHistory("function-select");
                return;
            case "overview":
                if (_overviewView.DataContext is null) _overviewView.DataContext = _overviewViewModel;
                PageHost.Content = _overviewView;
                RecordLeftHistory("overview");
                return;
            case "operation":
                if (_operationBar.DataContext is null) _operationBar.DataContext = _operationViewModel;
                PageHost.Content = _operationBar;
                RecordLeftHistory("operation");
                return;
            case "manual":
                if (_manualView.DataContext is null) _manualView.DataContext = _manualViewModel;
                PageHost.Content = _manualView;
                RecordLeftHistory("manual");
                return;
            case "parameters":
                if (_parametersView.DataContext is null) _parametersView.DataContext = _parametersViewModel;
                PageHost.Content = _parametersView;
                RecordLeftHistory("parameters");
                return;
            case "io-diagnostics":
                if (_ioDiagnosticsView.DataContext is null) _ioDiagnosticsView.DataContext = _ioDiagnosticsViewModel;
                PageHost.Content = _ioDiagnosticsView;
                RecordLeftHistory("io-diagnostics");
                return;
            case "diagnostic-terminal":
                if (_diagnosticTerminalView.DataContext is null) _diagnosticTerminalView.DataContext = _diagnosticTerminalViewModel;
                PageHost.Content = _diagnosticTerminalView;
                RecordLeftHistory("diagnostic-terminal");
                return;
            case "connection-settings":
                if (_connectionSettingsView.DataContext is null) _connectionSettingsView.DataContext = _connectionSettingsViewModel;
                PageHost.Content = _connectionSettingsView;
                RecordLeftHistory("connection-settings");
                return;
            case "history":
                if (_historyView.DataContext is null) _historyView.DataContext = _historyViewModel;
                PageHost.Content = _historyView;
                RecordLeftHistory("history");
                return;
        }

        // 尝试作为可配置页面 id 导航
        if (_configLayout?.FindPage(tag) is not null)
        {
            Navigate(tag);
            HighlightLeftNav(tag);
            return;
        }

        // 未知：占位
        ShowPlaceholder(tag, "待配置");
    }

    private void RecordLeftHistory(string tag)
    {
        if (_configHistory.Count == 0 || _configHistory[^1] != tag)
            _configHistory.Add(tag);
    }

    private void HighlightLeftNav(string? tag)
    {
        _currentLeftTag = tag;
        if (LeftNavPanel is null) return;
        foreach (var child in LeftNavPanel.Children)
        {
            if (child is Button b && b.Tag is string t)
            {
                var isSelected = string.Equals(t, tag, StringComparison.Ordinal);
                b.Style = TryFindResource(isSelected ? "HmiLeftNavSelectedStyle" : "HmiLeftNavButtonStyle") as Style
                          ?? b.Style;
            }
            else if (child is StackPanel sp && sp.Name == "LeftNavConfigContainer")
            {
                foreach (var inner in sp.Children)
                {
                    if (inner is Button ib && ib.Tag is string it)
                    {
                        var isSel = string.Equals(it, tag, StringComparison.Ordinal);
                        ib.Style = TryFindResource(isSel ? "HmiLeftNavSelectedStyle" : "HmiLeftNavButtonStyle") as Style
                                   ?? ib.Style;
                    }
                }
            }
        }
        HighlightBottomTabs(tag);
    }

    private void HighlightBottomTabs(string? tag)
    {
        if (BottomTabPanel is null) return;
        // 将左侧 tag 映射到对应的底部高亮键
        var map = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["history"] = "alarm",
            ["alarm-overview"] = "alarm",
            ["alarm"] = "alarm",
            ["io-diagnostics"] = "io-diagnostics",
            ["diagnostic-terminal"] = "diagnostic-terminal",
        };
        var bottomKey = tag != null && map.TryGetValue(tag, out var k) ? k : tag;
        foreach (var child in BottomTabPanel.Children)
        {
            if (child is Button b && b.Tag is string t)
            {
                var isSel = string.Equals(t, bottomKey, StringComparison.Ordinal);
                b.Style = TryFindResource(isSel ? "HmiBottomTabSelectedStyle" : "HmiBottomTabStyle") as Style
                          ?? b.Style;
            }
        }
    }

    // --- 右侧垂直命令列：手动/自动/启动/停止红/复位/初始化 -----------------------------------
    private async void OnRightManualClicked(object sender, RoutedEventArgs e) => await ExecuteManualModeAsync();
    private async void OnRightAutoClicked(object sender, RoutedEventArgs e) => await ExecuteAutoModeAsync();
    private async void OnRightStartClicked(object sender, RoutedEventArgs e) => await ExecutePulseAsync(CommandTarget.Start, "启动");
    private async void OnRightStopClicked(object sender, RoutedEventArgs e) => await ExecutePulseAsync(CommandTarget.Stop, "停止");
    private async void OnRightResetClicked(object sender, RoutedEventArgs e) => await ExecutePulseAsync(CommandTarget.Reset, "复位");
    private async void OnRightInitClicked(object sender, RoutedEventArgs e) => await ExecutePulseAsync(CommandTarget.Reset, "初始化");

    private async Task ExecuteManualModeAsync()
    {
        if (_configCommandService is null) return;
        // 手动：M104=0, M105=0 互斥组合
        var r1 = await _configCommandService.ExecuteAsync(new CommandRequest(CommandTarget.AutoMode, false), CancellationToken.None);
        var r2 = await _configCommandService.ExecuteAsync(new CommandRequest(CommandTarget.BypassMode, false), CancellationToken.None);
        ReportCommandIfFailed("手动模式", r1.Status == CommandStatus.Success ? r2 : r1);
    }

    private async Task ExecuteAutoModeAsync()
    {
        if (_configCommandService is null) return;
        // 自动：M104=1, M105=0
        var r1 = await _configCommandService.ExecuteAsync(new CommandRequest(CommandTarget.AutoMode, true), CancellationToken.None);
        var r2 = await _configCommandService.ExecuteAsync(new CommandRequest(CommandTarget.BypassMode, false), CancellationToken.None);
        ReportCommandIfFailed("自动模式", r1.Status == CommandStatus.Success ? r2 : r1);
    }

    private async Task ExecutePulseAsync(CommandTarget target, string label)
    {
        if (_configCommandService is null) return;
        try
        {
            var result = await _configCommandService.ExecuteAsync(new CommandRequest(target), CancellationToken.None);
            ReportCommandIfFailed(label, result);
        }
        catch (Exception ex)
        {
            ReportWarning($"{label}失败：{ex.Message}");
        }
    }

    private void ReportCommandIfFailed(string label, CommandResult result)
    {
        if (result.Status != CommandStatus.Success)
        {
            ReportWarning($"{label}：{result.Message ?? result.Status.ToString()}");
        }
    }

    private void ReportWarning(string text)
    {
        try
        {
            if (_configMainViewModel is not null)
                _configMainViewModel.WarningText = text;
        }
        catch { }
        // 兜底：若未注入 MainViewModel，短暂以 MessageBox 提示（仅失败路径）
        if (_configMainViewModel is null)
        {
            try { MessageBox.Show(text, "PLC 上位机", MessageBoxButton.OK, MessageBoxImage.Warning); } catch { }
        }
    }

    // --- 底部 tab 行：报警/前后交互/离线设置/延时处理/I-O/自动条件/诊断/工位视图/探针寿命 -----------
    private void OnBottomTabClicked(object sender, RoutedEventArgs e)
    {
        if (sender is not Button b || b.Tag is not string tag) return;
        ReleaseManualJogsOnSwitch();
        switch (tag)
        {
            case "alarm":
                NavigateToLeftPage("alarm-overview");
                HighlightBottomTabs("alarm");
                break;
            case "front-back":
                ShowPlaceholder("前后交互", "待配置");
                HighlightBottomTabs(tag);
                break;
            case "offline-set":
                // 离线设置 → 通信设置页
                NavigateToLeftPage("connection-settings");
                HighlightBottomTabs(tag);
                break;
            case "delay":
                ShowPlaceholder("延时处理", "待配置");
                HighlightBottomTabs(tag);
                break;
            case "io-diagnostics":
                NavigateToLeftPage("io-diagnostics");
                HighlightBottomTabs(tag);
                break;
            case "auto-cond":
                ShowPlaceholder("自动条件", "待配置");
                HighlightBottomTabs(tag);
                break;
            case "diagnostic-terminal":
                NavigateToLeftPage("diagnostic-terminal");
                HighlightBottomTabs(tag);
                break;
            case "station-view":
                ShowPlaceholder("工位视图", "待配置");
                HighlightBottomTabs(tag);
                break;
            case "probe-life":
                ShowPlaceholder("探针寿命", "待配置");
                HighlightBottomTabs(tag);
                break;
            default:
                ShowPlaceholder(tag, "待配置");
                HighlightBottomTabs(tag);
                break;
        }
    }

    private void ShowPlaceholder(string title, string detail)
    {
        var titleBlock = new TextBlock
        {
            Text = title,
            FontSize = 22,
            FontWeight = FontWeights.SemiBold,
            Foreground = (Brush)TryFindResource("ConfigUiTextBrush") ?? Brushes.White,
            Margin = new Thickness(0, 0, 0, 8),
        };
        var detailBlock = new TextBlock
        {
            Text = detail,
            FontSize = 16,
            Foreground = (Brush)TryFindResource("ConfigUiMutedTextBrush") ?? Brushes.Gray,
            Margin = new Thickness(0, 0, 0, 12),
        };
        var hint = new TextBlock
        {
            Text = "数据缺失显示“待配置” · 按威纶通深蓝 HMI 风格占位，后续可配置真实页面或在 ui-layout.json 中声明 pageHost。",
            FontSize = 12,
            Foreground = (Brush)TryFindResource("ConfigUiMutedTextBrush") ?? Brushes.Gray,
            Opacity = 0.85,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 8, 0, 0),
        };
        var panel = new StackPanel { Margin = new Thickness(28), VerticalAlignment = VerticalAlignment.Center };
        panel.Children.Add(titleBlock);
        panel.Children.Add(detailBlock);
        panel.Children.Add(hint);
        var card = new Border
        {
            Background = (Brush)TryFindResource("ConfigUiPanelBrush") ?? new SolidColorBrush(Color.FromRgb(0x10, 0x3E, 0x63)),
            BorderBrush = (Brush)TryFindResource("ConfigUiGridLineBrush") ?? Brushes.Gray,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(24),
            Margin = new Thickness(24),
            Child = panel,
        };
        var outer = new Grid { Background = (Brush)TryFindResource("ConfigUiBackgroundBrush") ?? Brushes.Transparent };
        outer.Children.Add(card);
        PageHost.Content = outer;
    }

    public void NavigateToIoDiagnostics() => NavigateToLeftPage("io-diagnostics");
    public void NavigateToDiagnosticTerminal() => NavigateToLeftPage("diagnostic-terminal");
    public void NavigateToPlaceholder(string title, string hint) => ShowPlaceholder(title, hint);

    private static FunctionSelectViewModel CreateDefaultFunctionSelectVm()
    {
        var gate = new TestOfflineGate();
        var client = new TestNoopModbusClient();
        var delay = new TaskDelay();
        var service = new CommandService(client, gate, delay);
        return new FunctionSelectViewModel(service, gate);
    }

    private sealed class TestOfflineGate : ICommandGate
    {
        public bool IsOnline => false;
        public bool IsManualIdle => false;
    }

    private sealed class TestNoopModbusClient : IModbusClient
    {
        public Task ConnectAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task DisconnectAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<bool[]> ReadCoilsAsync(byte slaveId, ushort startAddress, ushort count, CancellationToken cancellationToken) => Task.FromResult(Array.Empty<bool>());
        public Task<bool[]> ReadDiscreteInputsAsync(byte slaveId, ushort startAddress, ushort count, CancellationToken cancellationToken) => Task.FromResult(Array.Empty<bool>());
        public Task<ushort[]> ReadHoldingRegistersAsync(byte slaveId, ushort startAddress, ushort count, CancellationToken cancellationToken) => Task.FromResult(Array.Empty<ushort>());
        public Task<ushort[]> ReadInputRegistersAsync(byte slaveId, ushort startAddress, ushort count, CancellationToken cancellationToken) => Task.FromResult(Array.Empty<ushort>());
        public Task WriteSingleCoilAsync(byte slaveId, ushort address, bool value, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task WriteSingleRegisterAsync(byte slaveId, ushort address, ushort value, CancellationToken cancellationToken) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    // --- Configurable HMI shell (design §7 模块化可配置界面) ------------------------------------------
    private void SetupConfigurablePages()
    {
        try
        {
            var layoutPath = Path.Combine(AppContext.BaseDirectory, "config", "ui-layout.json");
            UiLayoutDefinition? layout;
            try
            {
                layout = UiLayoutLoader.TryLoadFromFile(layoutPath);
            }
            catch (Exception ex)
            {
                var logDir = Path.Combine(AppContext.BaseDirectory, "logs");
                try { PlcSoftware.App.Services.CrashReporter.Record(DateTime.Now, ex, logDir); } catch { }
                try
                {
                    MessageBox.Show(
                        $"ui-layout.json 加载失败，已回退到传统导航。\n{ex.Message}\n\n日志：{logDir}\n文件：{layoutPath}",
                        "PLC 上位机", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
                catch { }
                return;
            }

            if (layout is null) return;

            _configLayout = layout;

            var separator = new Border
            {
                Width = 1,
                Height = 20,
                Background = TryFindResource("InverseForegroundBrush") as Brush ?? System.Windows.Media.Brushes.Gray,
                Margin = new Thickness(8, 0, 8, 0),
                VerticalAlignment = VerticalAlignment.Center,
            };
            try { ConfigNavItems.Children.Add(separator); } catch { }

            var loginPageId = LoginPageId();
            foreach (var page in layout.Pages)
            {
                if (page.Id == loginPageId) continue;
                try
                {
                    var button = new Button
                    {
                        Content = string.IsNullOrWhiteSpace(page.Title) ? page.Id : page.Title,
                        Style = TryFindResource("NavButtonStyle") as Style,
                    };
                    var pageId = page.Id;
                    button.Click += (_, _) => Navigate(pageId);
                    ConfigNavItems.Children.Add(button);
                }
                catch { }
            }

            // 将可配置页面同步到左侧垂直导航（除已固定的 6 个 HMI 主页面外，其余动态追加）
            PopulateLeftNavFromConfig();

            UpdateGateUi();
            try
            {
                // 默认页：若为 home/新 HMI 页则走左侧直达，否则走可配置导航
                var def = layout.DefaultPage?.Id ?? "home";
                if (IsKnownHmiTag(def))
                    NavigateToLeftPage(def);
                else
                    Navigate(def);
            }
            catch (Exception ex)
            {
                var logDir = Path.Combine(AppContext.BaseDirectory, "logs");
                try { PlcSoftware.App.Services.CrashReporter.Record(DateTime.Now, ex, logDir); } catch { }
                try { MessageBox.Show($"初始导航失败：{ex.Message}\n\n日志：{logDir}", "PLC 上位机", MessageBoxButton.OK, MessageBoxImage.Error); } catch { }
            }
        }
        catch (Exception ex)
        {
            var logDir = Path.Combine(AppContext.BaseDirectory, "logs");
            try { PlcSoftware.App.Services.CrashReporter.Record(DateTime.Now, ex, logDir); } catch { }
            try { MessageBox.Show($"配置界面初始化失败：{ex.Message}\n\n日志：{logDir}", "PLC 上位机", MessageBoxButton.OK, MessageBoxImage.Error); } catch { }
        }
    }

    private bool IsKnownHmiTag(string id) => id is "home" or "alarm-overview" or "operation-record" or "cylinder-control" or "motor-control" or "function-select";

    private void PopulateLeftNavFromConfig()
    {
        if (_configLayout is null || LeftNavConfigContainer is null) return;
        var known = new HashSet<string>(StringComparer.Ordinal) { "home", "alarm-overview", "operation-record", "cylinder-control", "motor-control", "function-select", "login", "overview", "operation", "parameters", "io-diagnostics", "history", "position-loading", "diagnostic-terminal", "connection-settings", "manual" };
        var loginId = LoginPageId();
        foreach (var page in _configLayout.Pages)
        {
            if (page.Id == loginId) continue;
            if (known.Contains(page.Id)) continue;
            var btn = new Button
            {
                Content = string.IsNullOrWhiteSpace(page.Title) ? page.Id : page.Title,
                Style = TryFindResource("HmiLeftNavButtonStyle") as Style,
                Tag = page.Id,
            };
            var pid = page.Id;
            btn.Click += (_, _) => Navigate(pid);
            LeftNavConfigContainer.Children.Add(btn);
        }
    }

    private bool IsGateActive => _configLayout?.App.LoginRequired == true && !_isSignedIn;

    private void UpdateGateUi()
    {
        var gateActive = IsGateActive;
        try { ConfigNavItems.Visibility = gateActive ? Visibility.Collapsed : Visibility.Visible; } catch { }
        try
        {
            foreach (var child in NavButtonsPanel.Children)
            {
                if (child is Button btn)
                {
                    btn.IsEnabled = !gateActive;
                    btn.Visibility = gateActive ? Visibility.Collapsed : Visibility.Visible;
                }
            }
        }
        catch { }
        // 新 HMI 壳：门控时隐藏侧边/底部，仅保留中央登录页
        try
        {
            if (LeftNavPanel is not null) LeftNavPanel.Visibility = gateActive ? Visibility.Collapsed : Visibility.Visible;
            // 左侧 Border 的父级仍可见，仅内部 StackPanel 隐藏即可保持背景
            // 右侧命令列与底部 tab 同理
            if (RightCommandPanel is not null) RightCommandPanel.Visibility = gateActive ? Visibility.Collapsed : Visibility.Visible;
            if (BottomTabPanel is not null) BottomTabPanel.Visibility = gateActive ? Visibility.Collapsed : Visibility.Visible;
            // 也可通过外层 Border Visibility 控制（若需完全隐藏侧边栏背景，可访问 Parent）
            var leftBorder = LeftNavPanel?.Parent as Border;
            if (leftBorder != null) leftBorder.Visibility = gateActive ? Visibility.Collapsed : Visibility.Visible;
            var rightBorder = RightCommandPanel?.Parent as Border;
            if (rightBorder != null) rightBorder.Visibility = gateActive ? Visibility.Collapsed : Visibility.Visible;
            var bottomBorder = (BottomTabPanel?.Parent as FrameworkElement)?.Parent as Border;
            // BottomTabPanel 在 ScrollViewer 内，ScrollViewer 在 Border 内
            if (bottomBorder is null && BottomTabPanel?.Parent is ScrollViewer sv2) bottomBorder = (sv2.Parent as FrameworkElement) as Border;
            if (bottomBorder != null) bottomBorder.Visibility = gateActive ? Visibility.Collapsed : Visibility.Visible;
        }
        catch { }
    }

    public void Navigate(string pageId)
    {
        if (_configLayout is null) return;
        var page = _configLayout.FindPage(pageId);
        if (page is null) return;
        var loginPage = LoginPageId();
        if (_isSignedIn && pageId == loginPage) return;
        if (_configLayout.App.LoginRequired && !_isSignedIn && (pageId != loginPage || loginPage is null))
        {
            if (loginPage is null) return;
            NavigateTo(loginPage);
            return;
        }
        NavigateTo(pageId);
    }

    private void NavigateTo(string pageId)
    {
        if (_configLayout is null) return;
        ReleaseManualJogsOnSwitch();
        var loginPageHistory = LoginPageId();
        if (pageId == loginPageHistory)
        {
            if (_configHistory.Count == 0 || _configHistory[^1] != pageId) _configHistory.Add(pageId);
        }
        else
        {
            if (loginPageHistory is not null) _configHistory.RemoveAll(id => id == loginPageHistory);
            if (_configHistory.Count == 0 || _configHistory[^1] != pageId) _configHistory.Add(pageId);
        }

        if (!_configPageVms.TryGetValue(pageId, out var vm))
        {
            vm = new ConfigurablePageViewModel(_configLayout, _configLayout.FindPage(pageId)!,
                this, _configCommandService!, _configParameterService!, JsonTileStore.Default(), _configMainViewModel);
            _configPageVms[pageId] = vm;
        }
        if (!_configPageViews.TryGetValue(pageId, out var view))
        {
            view = new ConfigurablePageView();
            _configPageViews[pageId] = view;
        }
        view.SetHostedContent(ResolveLegacyView(vm.HostedViewName));
        view.Apply(vm);
        PageHost.Content = view;
        HighlightLeftNav(pageId);
    }

    public void SignIn(string username)
    {
        _isSignedIn = true;
        _signedInUser = username;
        try { SignedInText.Text = $"已登录：{username}"; } catch { }
        try { SignOutButton.Visibility = Visibility.Visible; } catch { }
        UpdateGateUi();
        HighlightLeftNav(_currentLeftTag ?? "home");
    }

    public void SignOut()
    {
        _isSignedIn = false;
        _signedInUser = null;
        try { SignedInText.Text = string.Empty; } catch { }
        try { SignOutButton.Visibility = Visibility.Collapsed; } catch { }
        foreach (var vm in _configPageVms.Values) vm.SignOut();
        UpdateGateUi();
        if (_configLayout is not null && LoginPageId() is { } login) NavigateTo(login);
    }

    private void OnSignOutClicked(object sender, RoutedEventArgs e) => SignOut();

    private string? LoginPageId()
        => _configLayout?.Pages.FirstOrDefault(p => p.Modules.Any(m => m.Type == UiModuleType.LoginForm))?.Id;

    private string? CurrentConfigPageId()
        => _configHistory.Count > 0 ? _configHistory[^1] : null;

    public void NavigateUp()
    {
        if (_configLayout is null) return;
        var loginPage = LoginPageId();
        var current = CurrentConfigPageId();
        var index = _configLayout.Pages.FindIndex(p => p.Id == current);
        if (index <= 0) return;
        var prev = index - 1;
        while (prev >= 0 && _isSignedIn && _configLayout.Pages[prev].Id == loginPage) prev--;
        if (prev < 0) return;
        Navigate(_configLayout.Pages[prev].Id);
    }

    public void NavigateDown()
    {
        if (_configLayout is null) return;
        var loginPage = LoginPageId();
        var current = CurrentConfigPageId();
        var index = _configLayout.Pages.FindIndex(p => p.Id == current);
        if (index < 0 || index >= _configLayout.Pages.Count - 1) return;
        var next = index + 1;
        while (next < _configLayout.Pages.Count && _isSignedIn && _configLayout.Pages[next].Id == loginPage) next++;
        if (next >= _configLayout.Pages.Count) return;
        Navigate(_configLayout.Pages[next].Id);
    }

    public void NavigateBack()
    {
        if (_configHistory.Count <= 1) return;
        var loginPage = LoginPageId();
        _configHistory.RemoveAt(_configHistory.Count - 1);
        while (_configHistory.Count > 0 && _isSignedIn && _configHistory[^1] == loginPage) _configHistory.RemoveAt(_configHistory.Count - 1);
        if (_configHistory.Count == 0) return;
        Navigate(_configHistory[^1]);
    }

    public void ShowLogin()
    {
        if (_configLayout is null) return;
        var loginPage = _configLayout.Pages.FirstOrDefault(p => p.Modules.Any(m => m.Type == UiModuleType.LoginForm));
        if (loginPage is not null) Navigate(loginPage.Id);
    }

    private FrameworkElement? ResolveLegacyView(string? viewName)
        => viewName switch
        {
            "OverviewView" => _overviewView,
            "OperationBar" => _operationBar,
            "ManualView" => _manualView,
            "ParametersView" => _parametersView,
            "IoDiagnosticsView" => _ioDiagnosticsView,
            "DiagnosticTerminalView" => _diagnosticTerminalView,
            "ConnectionSettingsView" => _connectionSettingsView,
            "HistoryView" => _historyView,
            "HomeView" => _homeView,
            "FunctionSelectView" => _functionSelectView,
            "CylinderControlView" => _cylinderControlView,
            "AlarmOverviewView" => _alarmOverviewView,
            "OperationRecordView" => _operationRecordView,
            "MotorControlView" => _motorControlView,
            _ => null,
        };
}
