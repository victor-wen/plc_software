using System.IO;
using System.Text;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PlcSoftware.App.Services;
using PlcSoftware.App.ViewModels;
using PlcSoftware.App.Views;
using PlcSoftware.Core.Abstractions;
using PlcSoftware.Core.Configuration;
using PlcSoftware.Core.Models;
using PlcSoftware.Core.Services;
using PlcSoftware.Infrastructure.Configuration;
using PlcSoftware.Infrastructure.Modbus;
using PlcSoftware.Infrastructure.Simulation;

namespace PlcSoftware.App;

/// <summary>
/// WPF application composition root. Builds the Microsoft.Extensions.Hosting Generic Host, registers
/// every Core/Infrastructure service (the Modbus transport defaults to the in-memory simulation, per
/// the plan: 离线阶段默认使用模拟点表), registers the view models, wires the event fan-out into the
/// view model, and shows the main window.
/// </summary>
public partial class App : Application
{
    private IHost? _host;

    private SingleInstanceGuard? _instanceGuard;

    protected override void OnStartup(StartupEventArgs e)
    {
        // 记录崩溃日志（不阻断崩溃路径，仅落盘）
        var logDir = Path.Combine(AppContext.BaseDirectory, "logs");
        try { CrashReporter.Attach(logDir); } catch { }

        // 全局未处理异常 -> 落盘 + 弹框（避免直接闪退无信息）
        DispatcherUnhandledException += (_, args) =>
        {
            try { CrashReporter.Record(DateTime.Now, args.Exception, logDir); } catch { }
            try
            {
                MessageBox.Show(
                    $"发生未处理异常：{args.Exception.Message}\n\n详情已写入 {logDir}\n\n{args.Exception}",
                    "PLC 上位机",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
            catch { }
            args.Handled = true;
            try { Shutdown(1); } catch { }
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            try { CrashReporter.Record(DateTime.Now, args.ExceptionObject as Exception, logDir); } catch { }
        };
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            try { CrashReporter.Record(DateTime.Now, args.Exception, logDir); } catch { }
            args.SetObserved();
        };

        // 单实例：第二实例直接提示后退出，避免串口争用
        try
        {
            _instanceGuard = new SingleInstanceGuard("Global\\PlcSoftware.SingleInstance");
            if (!_instanceGuard.TryAcquire())
            {
                MessageBox.Show("程序已在运行中。", "PLC 上位机", MessageBoxButton.OK, MessageBoxImage.Information);
                Shutdown(0);
                return;
            }
        }
        catch (Exception ex)
        {
            try { CrashReporter.Record(DateTime.Now, ex, logDir); } catch { }
            // 单实例本身异常不阻断启动
        }

        base.OnStartup(e);

        try
        {
            _host = Host.CreateDefaultBuilder()
                .ConfigureLogging(logging => logging.ClearProviders())
                .ConfigureServices(ConfigureServices)
                .Build();

            // Start the Generic Host: this launches the PlcRuntime hosted service (connection-supervision,
            // polling and D106 watchdog loops) and the SimulationScenarioDriver (demo scenario replay).
            _host.Start();
        }
        catch (Exception ex)
        {
            try { CrashReporter.Record(DateTime.Now, ex, logDir); } catch { }
            MessageBox.Show(
                $"启动失败：{ex.Message}\n\n日志：{logDir}\n\n{ex}",
                "PLC 上位机",
                MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
            return;
        }

        // Wire the state fan-out to the view model. The view model stays UI-thread-free; updates are
        // marshalled to the application dispatcher so WPF bindings observe the changes on the UI thread.
        var viewModel = _host.Services.GetRequiredService<MainViewModel>();
        var supervisor = _host.Services.GetRequiredService<ConnectionSupervisor>();
        var store = _host.Services.GetRequiredService<IDeviceStateStore>();
        var heartbeat = _host.Services.GetRequiredService<HeartbeatMonitor>();
        var heldState = _host.Services.GetRequiredService<SimpleHeldStateService>();

        supervisor.StateChanged += state => RunOnUi(() => viewModel.ApplyConnectionState(state));
        store.SnapshotChanged += (_, snapshot) => RunOnUi(() => viewModel.ApplySnapshot(snapshot));
        heartbeat.StatusChanged += status => RunOnUi(() => viewModel.ApplyHeartbeat(status));
        heldState.MaskStateChanged += () => RunOnUi(() =>
            viewModel.ApplyMaskState(heldState.LightCurtainBypass, heldState.DoorBypass));

        // Overview page (design §6.2): a dedicated read-only view model that only consumes the decoded
        // snapshot and the supervised link state. Wired exactly like MainViewModel — no new service.
        var overview = _host.Services.GetRequiredService<OverviewViewModel>();
        supervisor.StateChanged += state => RunOnUi(() => overview.ApplyConnectionState(state));
        store.SnapshotChanged += (_, snapshot) => RunOnUi(() => overview.ApplySnapshot(snapshot));

        // Operation zone (design §6.3): executes host commands through the composition-root ICommandService
        // and gates them through the injected ICommandGate (AppCommandGate). It only consumes the snapshot +
        // link state for the CanExecute pre-gate, so it is wired exactly like the other pages.
        var operation = _host.Services.GetRequiredService<OperationViewModel>();
        supervisor.StateChanged += state => RunOnUi(() => operation.ApplyConnectionState(state));
        store.SnapshotChanged += (_, snapshot) => RunOnUi(() => operation.ApplySnapshot(snapshot));

        // Manual page (design §6.4): press-and-hold jogs through the same ICommandService / ICommandGate.
        // It only consumes the snapshot + link state for the CanExecute pre-gate and the header text.
        var manual = _host.Services.GetRequiredService<ManualViewModel>();
        supervisor.StateChanged += state => RunOnUi(() => manual.ApplyConnectionState(state));
        store.SnapshotChanged += (_, snapshot) => RunOnUi(() => manual.ApplySnapshot(snapshot));

        // Parameter page (design §6.5): writes through the injected ParameterService (FC06 write + FC03
        // read-back verify) gated by the injected ICommandGate. It consumes the snapshot for the current
        // (old) value of the editable D201/D202/D204/D205 and the read-only D203/D210/D212.D213 displays.
        var parameters = _host.Services.GetRequiredService<ParametersViewModel>();
        supervisor.StateChanged += state => RunOnUi(() => parameters.ApplyConnectionState(state));
        store.SnapshotChanged += (_, snapshot) => RunOnUi(() => parameters.ApplySnapshot(snapshot));

        // Diagnostic terminal page (design §6.5): only the link state refreshes the header and the write
        // pre-gate; the actual command timing/hex/elapsed presentation is an explicit user action.
        var diagnosticTerminal = _host.Services.GetRequiredService<DiagnosticTerminalViewModel>();
        supervisor.StateChanged += state => RunOnUi(() => diagnosticTerminal.ApplyConnectionState(state));

        // I/O diagnostics page (design §6.6): read-only X/Y/M table fed only by the snapshot + link state.
        var ioDiagnostics = _host.Services.GetRequiredService<IoDiagnosticsViewModel>();
        supervisor.StateChanged += state => RunOnUi(() => ioDiagnostics.ApplyConnectionState(state));
        store.SnapshotChanged += (_, snapshot) => RunOnUi(() => ioDiagnostics.ApplySnapshot(snapshot));

        // Communication settings page (design §6.8): consumes only the link state (to lock the form while
        // online). Its connection test is a self-contained explicit action — no snapshot needed.
        var connectionSettings = _host.Services.GetRequiredService<ConnectionSettingsViewModel>();
        supervisor.StateChanged += state => RunOnUi(() => connectionSettings.ApplyConnectionState(state));

        // 主页面 / 功能选择 / 气缸控制：遵循 OverviewViewModel 模式（WPF-free，仅 Snapshot/Connection）
        var home = _host.Services.GetRequiredService<HomeViewModel>();
        supervisor.StateChanged += state => RunOnUi(() => home.ApplyConnectionState(state));
        store.SnapshotChanged += (_, snapshot) => RunOnUi(() => home.ApplySnapshot(snapshot));

        var functionSelect = _host.Services.GetRequiredService<FunctionSelectViewModel>();
        supervisor.StateChanged += state => RunOnUi(() => functionSelect.ApplyConnectionState(state));
        store.SnapshotChanged += (_, snapshot) => RunOnUi(() => functionSelect.ApplySnapshot(snapshot));

        var cylinderControl = _host.Services.GetRequiredService<CylinderControlViewModel>();
        supervisor.StateChanged += state => RunOnUi(() => cylinderControl.ApplyConnectionState(state));
        store.SnapshotChanged += (_, snapshot) => RunOnUi(() => cylinderControl.ApplySnapshot(snapshot));

        // 操作记录 / 报警总览 / 电机控制（威纶通深蓝占位，WPF-free）
        var operationRecord = _host.Services.GetRequiredService<OperationRecordViewModel>();
        supervisor.StateChanged += state => RunOnUi(() => operationRecord.ApplyConnectionState(state));

        var alarmOverview = _host.Services.GetRequiredService<AlarmOverviewViewModel>();
        supervisor.StateChanged += state => RunOnUi(() => alarmOverview.ApplyConnectionState(state));

        var motorControl = _host.Services.GetRequiredService<MotorControlViewModel>();
        supervisor.StateChanged += state => RunOnUi(() => motorControl.ApplyConnectionState(state));
        store.SnapshotChanged += (_, snapshot) => RunOnUi(() => motorControl.ApplySnapshot(snapshot));

        // Seed once after subscribing so an event raised before the subscription (or before the host start
        // finished) is not lost — the first StateChanged/SnapshotChanged/StatusChanged can fire while the
        // hosted loops are still starting, before the wiring above is in place. Latest state wins over any
        // racing event.
        viewModel.ApplyConnectionState(supervisor.CurrentState);
        viewModel.ApplyHeartbeat(heartbeat.Status);
        viewModel.ApplySnapshot(store.Current);
        viewModel.ApplyMaskState(heldState.LightCurtainBypass, heldState.DoorBypass);
        overview.ApplyConnectionState(supervisor.CurrentState);
        overview.ApplySnapshot(store.Current);
        operation.ApplyConnectionState(supervisor.CurrentState);
        operation.ApplySnapshot(store.Current);
        manual.ApplyConnectionState(supervisor.CurrentState);
        manual.ApplySnapshot(store.Current);
        parameters.ApplyConnectionState(supervisor.CurrentState);
        parameters.ApplySnapshot(store.Current);
        diagnosticTerminal.ApplyConnectionState(supervisor.CurrentState);
        ioDiagnostics.ApplyConnectionState(supervisor.CurrentState);
        ioDiagnostics.ApplySnapshot(store.Current);
        connectionSettings.ApplyConnectionState(supervisor.CurrentState);
        home.ApplyConnectionState(supervisor.CurrentState);
        home.ApplySnapshot(store.Current);
        functionSelect.ApplyConnectionState(supervisor.CurrentState);
        functionSelect.ApplySnapshot(store.Current);
        cylinderControl.ApplyConnectionState(supervisor.CurrentState);
        cylinderControl.ApplySnapshot(store.Current);
        operationRecord.ApplyConnectionState(supervisor.CurrentState);
        alarmOverview.ApplyConnectionState(supervisor.CurrentState);
        motorControl.ApplyConnectionState(supervisor.CurrentState);
        motorControl.ApplySnapshot(store.Current);

        var window = _host.Services.GetRequiredService<MainWindow>();
        window.DataContext = viewModel;
        MainWindow = window;
        window.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // Joining and disposing the host is best-effort on app exit so a slow background loop cannot
        // hang shutdown indefinitely.
        try { _host?.StopAsync().GetAwaiter().GetResult(); } catch { }
        try { _host?.Dispose(); } catch { }
        try { _instanceGuard?.Dispose(); } catch { }
        base.OnExit(e);
    }

    private static void ConfigureServices(IServiceCollection services)
    {
        // Injectable time + audit, provided by App-level adapters.
        services.AddSingleton<IAsyncDelay, TaskDelay>();
        services.AddSingleton<IAuditLog, ConsoleAuditLog>();

        // Load the K1-K7 fault table (drives AlarmService and the view model's fault text).
        var configDir = Path.Combine(AppContext.BaseDirectory, "config");
        var loader = new JsonConfigurationLoader();
        IReadOnlyList<FaultDefinition> faults = Array.Empty<FaultDefinition>();
        SerialConnectionOptions serialOptions = new SerialConnectionOptions();
        IReadOnlyList<PointDefinition> pointMap = Array.Empty<PointDefinition>();
        var logDirForConfig = Path.Combine(AppContext.BaseDirectory, "logs");
        try
        {
            faults = loader.LoadFaults(Path.Combine(configDir, "faults.json"));
        }
        catch (Exception ex)
        {
            try { CrashReporter.Record(DateTime.Now, ex, logDirForConfig); } catch { }
            // 故障表缺失不闪退：用空表（D110 仍显示故障码数字），后续弹窗已在 OnStartup 外层兜住
            faults = Array.Empty<FaultDefinition>();
            try
            {
                MessageBox.Show($"faults.json 加载失败，已用空表启动。\n{ex.Message}\n\n日志：{logDirForConfig}", "PLC 上位机", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            catch { }
        }
        try
        {
            serialOptions = loader.LoadSerialOptions(Path.Combine(configDir, "appsettings.json"));
        }
        catch (Exception ex)
        {
            try { CrashReporter.Record(DateTime.Now, ex, logDirForConfig); } catch { }
            serialOptions = new SerialConnectionOptions();
            try
            {
                MessageBox.Show($"appsettings.json 加载失败，已用默认串口配置启动。\n{ex.Message}\n\n日志：{logDirForConfig}", "PLC 上位机", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            catch { }
        }
        try
        {
            pointMap = loader.LoadPointMap(Path.Combine(configDir, "point-map.simulation.json"));
        }
        catch (Exception ex)
        {
            try { CrashReporter.Record(DateTime.Now, ex, logDirForConfig); } catch { }
            pointMap = Array.Empty<PointDefinition>();
            try
            {
                MessageBox.Show($"point-map.simulation.json 加载失败，I/O 诊断为空。\n{ex.Message}\n\n日志：{logDirForConfig}", "PLC 上位机", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            catch { }
        }

        // Modbus transport: in-memory simulation behind the shared single-flight queue (design §5.1). The
        // concrete InMemoryModbusClient is registered so the demo scenario runner can drive its memory
        // directly while the polling/command paths go through the QueuedModbusClient decorator — all
        // sharing the one memory instance.
        services.AddSingleton<SimulationMemory>();
        services.AddSingleton(sp => new InMemoryModbusClient(sp.GetRequiredService<SimulationMemory>()));
        services.AddSingleton<IModbusClient>(sp => new QueuedModbusClient(sp.GetRequiredService<InMemoryModbusClient>()));
        services.AddSingleton<ISupervisedConnection, ModbusSupervisedConnection>();

        // Demo scenario driver: advances the simulation clock on a 250 ms cadence so the heartbeat (D101),
        // step pointer (D200/D102/M200-M205) and counters are alive instead of frozen. Without it the demo
        // showed a static snapshot and the UI contradicted itself (在线 + 心跳丢失). Fully in-memory.
        services.AddSingleton(sp => new SimulationScenarioRunner(
            BuildDefaultDemoScenario(), sp.GetRequiredService<InMemoryModbusClient>()));
        services.AddSingleton<SimulationScenarioDriver>();
        services.AddHostedService(sp => sp.GetRequiredService<SimulationScenarioDriver>());

        // Core supervision / polling / state services.
        services.AddSingleton(sp => new ConnectionSupervisor(
            sp.GetRequiredService<ISupervisedConnection>(),
            sp.GetRequiredService<IAsyncDelay>()));
        services.AddSingleton(PollingPlan.Default());
        services.AddSingleton(sp => new PollingService(
            sp.GetRequiredService<PollingPlan>(),
            sp.GetRequiredService<IModbusClient>(),
            sp.GetRequiredService<IAsyncDelay>()));
        services.AddSingleton<IDeviceStateStore, DeviceStateStore>();
        services.AddSingleton(sp => new SnapshotMerger(sp.GetRequiredService<IDeviceStateStore>()));
        services.AddSingleton<HeartbeatMonitor>();
        services.AddSingleton(sp => new AlarmService(faults));

        // Host write services (all gated by the AppCommandGate, audited via IAuditLog).
        services.AddSingleton<ICommandGate, AppCommandGate>();
        services.AddSingleton(sp => new CommandService(
            sp.GetRequiredService<IModbusClient>(),
            sp.GetRequiredService<ICommandGate>(),
            sp.GetRequiredService<IAsyncDelay>(),
            auditLog: sp.GetRequiredService<IAuditLog>()));
        // The mask-aware command decorator records M110/M111 holding outcomes into the held-state tracker
        // (the App-layer source of the 屏蔽 flags). M110/M111 are holding commands, not feedback points:
        // they have no slot in the fast-block register map, so the tracker — not a snapshot — drives the UI.
        services.AddSingleton<SimpleHeldStateService>();
        services.AddSingleton<ICommandService>(sp => new CommandServiceMaskAware(
            sp.GetRequiredService<CommandService>(),
            sp.GetRequiredService<SimpleHeldStateService>()));
        services.AddSingleton(sp => new ParameterService(
            sp.GetRequiredService<IModbusClient>(),
            sp.GetRequiredService<ICommandGate>(),
            BuildWritableParameters(),
            auditLog: sp.GetRequiredService<IAuditLog>()));
        services.AddSingleton(sp => new HmiWatchdogService(
            sp.GetRequiredService<IModbusClient>(),
            sp.GetRequiredService<ICommandGate>(),
            sp.GetRequiredService<IAsyncDelay>()));

        // Diagnostic terminal (design §6.5): structured FC01/02/03/04 reads and FC05/06 single-point
        // writes over the shared single-queue client (so the terminal cannot bypass the request queue),
        // gated by the 5-minute unlock and the machine-running provider. Writes execute through the App
        // read-only view of link/machine state.
        services.AddSingleton(sp => new DiagnosticTerminalService(
            sp.GetRequiredService<IModbusClient>(),
            auditLog: sp.GetRequiredService<IAuditLog>(),
            isRunningProvider: () => AppCommandGate.ReadRunState(
                sp.GetRequiredService<IDeviceStateStore>().Current.Values)));

        // Coordinator + runtime (starts the background loops as the hosted service).
        services.AddSingleton(sp => new SnapshotCoordinator(
            sp.GetRequiredService<SnapshotMerger>(),
            sp.GetRequiredService<PollingService>()));
        services.AddSingleton<PlcRuntime>();
        services.AddHostedService(sp => sp.GetRequiredService<PlcRuntime>());

        // View model(s) + main window. The overview view model is a WPF-free consumer of the same
        // DeviceStateStore / ConnectionSupervisor the main view model reads; the main window takes it
        // (plus the overview view) via constructor injection so the 总览 nav entry shows the page.
        services.AddSingleton(sp => new MainViewModel(
            faults.ToDictionary(f => f.Code, f => f.Message)));
        services.AddSingleton<OverviewViewModel>();
        services.AddSingleton<OverviewView>();
        services.AddSingleton<OperationViewModel>();
        services.AddSingleton<OperationBar>();
        services.AddSingleton<ManualViewModel>();
        services.AddSingleton<ManualView>();
        services.AddSingleton(sp => new ParametersViewModel(
            sp.GetRequiredService<ParameterService>(),
            sp.GetRequiredService<ICommandGate>(),
            BuildWritableParameters()));
        services.AddSingleton<ParametersView>();

        // Diagnostic terminal page (design §6.5): FC01/02/03/04 reads + FC05/06 writes through the injected
        // DiagnosticTerminalService. It only consumes the link state for the header / write pre-gate, so it
        // is wired exactly like the other pages.
        services.AddSingleton(sp => new DiagnosticTerminalViewModel(
            sp.GetRequiredService<DiagnosticTerminalService>(),
            sp.GetRequiredService<ICommandGate>()));
        services.AddSingleton<DiagnosticTerminalView>();

        // I/O diagnostics page (design §6.6): read-only X/Y/M presentation. The point map supplies the raw X/Y
        // coil names (config/point-map.simulation.json); the view model matches the snapshot where a mirror
        // exists and shows 未上报 otherwise. No write path (Gate 7).
        services.AddSingleton(new IoDiagnosticsViewModel(pointMap));
        services.AddSingleton<IoDiagnosticsView>();

        // Communication settings page (design §6.8): serial options editing, validation and a connection test.
        // The connection test is the ONLY real-port touch — it stays behind an explicit user action and probes
        // through the SerialPortFactory seam (IConnectionTester); the production transport (polling/supervision)
        // is NOT wired here (the demo runs on the in-memory simulation). Editing is locked while Online.
        services.AddSingleton<IConnectionTester, SerialConnectionTester>();
        services.AddSingleton(sp => new ConnectionSettingsViewModel(
            sp.GetRequiredService<IConnectionTester>(),
            sp.GetRequiredService<ISupervisedConnection>(),
            serialOptions));
        services.AddSingleton<ConnectionSettingsView>();

        // 主页面 / 功能选择 / 气缸控制（新增 HMI，遵循 OverviewViewModel 模式：INotifyPropertyChanged + ApplySnapshot/ApplyConnectionState，WPF-free 可测试）
        services.AddSingleton<HomeViewModel>();
        services.AddSingleton<HomeView>();
        services.AddSingleton(sp => new FunctionSelectViewModel(
            sp.GetRequiredService<ICommandService>(),
            sp.GetRequiredService<ICommandGate>()));
        services.AddSingleton<FunctionSelectView>();
        services.AddSingleton<CylinderControlViewModel>();
        services.AddSingleton<CylinderControlView>();

        // Shared persistence (single WAL file) for History + new HMI pages. A single SqliteDatabase is shared
        // so the WAL busy timeout and schema are honoured across all readers; Alarm/Audit repositories are pure
        // wrappers over it. A DB failure never touches the polling path (design §7).
        services.AddSingleton(sp =>
        {
            var dbPath = Path.Combine(AppContext.BaseDirectory, "data", "history.db");
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(dbPath))!);
            var db = new PlcSoftware.Infrastructure.Persistence.SqliteDatabase(dbPath);
            db.EnsureSchema();
            return db;
        });
        services.AddSingleton(sp => new PlcSoftware.Infrastructure.Persistence.AlarmRepository(
            sp.GetRequiredService<PlcSoftware.Infrastructure.Persistence.SqliteDatabase>()));
        services.AddSingleton(sp => new PlcSoftware.Infrastructure.Persistence.AuditRepository(
            sp.GetRequiredService<PlcSoftware.Infrastructure.Persistence.SqliteDatabase>()));

        // 报警与历史 page (design §7): date-range query of persisted alarm + host-write audit rows and CSV
        // export. The HistoryViewModel is WPF-free and takes injected query functions; persistence is via the
        // shared SqliteDatabase (local history file). A database failure surfaces on the page's status text — it can
        // never stop the polling loops (the database is NOT on the polling path).
        services.AddSingleton(sp =>
        {
            var alarms = sp.GetRequiredService<PlcSoftware.Infrastructure.Persistence.AlarmRepository>();
            var audits = sp.GetRequiredService<PlcSoftware.Infrastructure.Persistence.AuditRepository>();
            return new HistoryViewModel(
                queryAlarms: (from, to) => alarms.QueryOpened(from, to)
                    .Select(r => new HistoryRow(
                        Timestamp: DateTime.TryParse(r["opened_at"] as string, null, System.Globalization.DateTimeStyles.RoundtripKind, out var op)
                            ? op
                            : DateTime.Now,
                        Kind: "报警",
                        Description: $"{r["code"]} {r["message"]}",
                        Value: r["closed_at"] is string ca ? $"已恢复 {ca}" : "活动中"))
                    .ToList(),
                queryAudits: (from, to) => audits.QueryRange(from, to)
                    .Select(r => new HistoryRow(
                        Timestamp: DateTime.TryParse(r["recorded_at"] as string, null, System.Globalization.DateTimeStyles.RoundtripKind, out var rt)
                            ? rt
                            : DateTime.Now,
                        Kind: r["category"]?.ToString() ?? "操作",
                        Description: r["target"]?.ToString() ?? "",
                        Value: r["value_text"]?.ToString()))
                    .ToList(),
                saveFile: (fileName, content) =>
                {
                    var dialog = new Microsoft.Win32.SaveFileDialog
                    {
                        FileName = fileName,
                        Filter = "CSV 文件 (*.csv)|*.csv",
                    };
                    if (dialog.ShowDialog() != true)
                    {
                        return;
                    }
                    File.WriteAllText(dialog.FileName, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
                });
        });
        services.AddSingleton<HistoryView>();

        // 操作记录页（威纶通深蓝 HMI 占位，表格：日期/时间/用户/端口/描述/类型）。
        // 数据来自 AuditRepository 查询结果映射为 OperationRecordRow（UI 占位显示），可通过 UsePlaceholderWhenEmpty 在空库时演示表格样式。
        services.AddSingleton(sp =>
        {
            var audits = sp.GetRequiredService<PlcSoftware.Infrastructure.Persistence.AuditRepository>();
            return new OperationRecordViewModel(
                (from, to) => audits.QueryRange(from, to)
                    .Select(OperationRecordViewModel.MapAuditRow)
                    .ToList());
        });
        services.AddSingleton<OperationRecordView>();

        // 报警总览页（威纶通深蓝 HMI 占位，表格：日期/时间/文本）。
        // 数据来自 AlarmRepository + 模拟文本（离线模式/扫码枪屏蔽/安全门屏蔽/光栅屏蔽 三级警告，格式
        // "三级警告: DB400."400 Alarm".Alarm3[4] 离线模式"），模拟文本可通过 SimulatedTexts 配置。
        services.AddSingleton(sp =>
        {
            var alarms = sp.GetRequiredService<PlcSoftware.Infrastructure.Persistence.AlarmRepository>();
            return new AlarmOverviewViewModel(
                (from, to) => alarms.QueryOpened(from, to)
                    .Select(r => AlarmOverviewViewModel.MapAlarmRow(r))
                    .ToList());
        });
        services.AddSingleton<AlarmOverviewView>();

        // 电机控制占位页：实时显示 D126 调宽速度/D122 皮带速度/D136 脉冲数/D138 产量等，点击跳转参数页。
        services.AddSingleton<MotorControlViewModel>();
        services.AddSingleton<MotorControlView>();

        services.AddSingleton<MainWindow>();
    }

    /// <summary>
    /// The writable engineering parameters (D126/D128/D204/D122, updated register map). Ranges are the
    /// configured allowed limits; until they are sourced from config they use repository defaults.
    /// Only parameters with a valid configured range are writable (ParameterService rejects otherwise).
    /// D124/D220 调宽速度设定值(mm/s) are display-only (duplicate label resolved UI-side).
    /// </summary>
    private static IEnumerable<ParameterDefinition> BuildWritableParameters()
        => new[]
        {
            new ParameterDefinition { Name = "D126", Address = 26, Unit = "Hz", Min = 0, Max = 1000 },    // 调宽速度
            new ParameterDefinition { Name = "D128", Address = 28, Unit = "mm", Min = 0, Max = 3000 },    // 目标宽度
            new ParameterDefinition { Name = "D204", Address = 104, Unit = "脉冲/mm", Min = 0, Max = 10000 }, // 脉冲当量
            new ParameterDefinition { Name = "D122", Address = 22, Unit = "Hz", Min = 0, Max = 1000 },    // 皮带速度
        };

    /// <summary>
    /// The default demo scenario for the in-memory simulation: the automatic-flow step pointer cycles
    /// 0..5 (one step per second, driving D120 / D102 / M200-M205) and the D140 heartbeat increments once
    /// per second. There are deliberately <em>no</em> fault or connect/disconnect events — a clean, alive
    /// run (the fix for the 在线 + 心跳丢失 contradiction). Fully in-memory; deterministic under the driver's
    /// virtual clock.
    /// </summary>
    private static SimulationScenario BuildDefaultDemoScenario()
    {
        var stepCount = (int)SimulationPoints.StepFlagCount; // 6 steps (0..5).
        var events = new List<SimulationEvent>();
        for (var second = 0; second < 60; second++)
        {
            events.Add(new SetStepEvent(TimeSpan.FromSeconds(second), (ushort)(second % stepCount)));
        }

        return new SimulationScenario(
            events,
            new SimulationHeartbeat(SimulationPoints.Heartbeat, TimeSpan.FromSeconds(1)));
    }

    private static void RunOnUi(Action action)
    {
        var dispatcher = Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            action();
        }
        else
        {
            dispatcher.BeginInvoke(action);
        }
    }
}
