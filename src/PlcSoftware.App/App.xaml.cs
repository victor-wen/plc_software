using System.IO;
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

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _host = Host.CreateDefaultBuilder()
            .ConfigureLogging(logging => logging.ClearProviders())
            .ConfigureServices(ConfigureServices)
            .Build();

        // Start the Generic Host: this launches the PlcRuntime hosted service, which starts the
        // connection-supervision, polling and D106 watchdog loops.
        _host.Start();

        // Wire the state fan-out to the view model. The view model stays UI-thread-free; updates are
        // marshalled to the application dispatcher so WPF bindings observe the changes on the UI thread.
        var viewModel = _host.Services.GetRequiredService<MainViewModel>();
        var supervisor = _host.Services.GetRequiredService<ConnectionSupervisor>();
        var store = _host.Services.GetRequiredService<IDeviceStateStore>();
        var heartbeat = _host.Services.GetRequiredService<HeartbeatMonitor>();

        supervisor.StateChanged += state => RunOnUi(() => viewModel.ApplyConnectionState(state));
        store.SnapshotChanged += (_, snapshot) => RunOnUi(() => viewModel.ApplySnapshot(snapshot));
        heartbeat.StatusChanged += status => RunOnUi(() => viewModel.ApplyHeartbeat(status));

        var window = _host.Services.GetRequiredService<MainWindow>();
        window.DataContext = viewModel;
        MainWindow = window;
        window.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // Joining and disposing the host is best-effort on app exit so a slow background loop cannot
        // hang shutdown indefinitely.
        _host?.StopAsync().GetAwaiter().GetResult();
        _host?.Dispose();
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
        var faults = loader.LoadFaults(Path.Combine(configDir, "faults.json"));

        // Modbus transport: in-memory simulation behind the shared single-flight queue (design §5.1).
        services.AddSingleton<SimulationMemory>();
        services.AddSingleton<IModbusClient>(sp => new QueuedModbusClient(
            new InMemoryModbusClient(sp.GetRequiredService<SimulationMemory>())));
        services.AddSingleton<ISupervisedConnection, ModbusSupervisedConnection>();

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
        services.AddSingleton(sp => new ParameterService(
            sp.GetRequiredService<IModbusClient>(),
            sp.GetRequiredService<ICommandGate>(),
            BuildWritableParameters(),
            auditLog: sp.GetRequiredService<IAuditLog>()));
        services.AddSingleton(sp => new HmiWatchdogService(
            sp.GetRequiredService<IModbusClient>(),
            sp.GetRequiredService<ICommandGate>(),
            sp.GetRequiredService<IAsyncDelay>()));

        // Coordinator + runtime (starts the background loops as the hosted service).
        services.AddSingleton(sp => new SnapshotCoordinator(
            sp.GetRequiredService<SnapshotMerger>(),
            sp.GetRequiredService<PollingService>()));
        services.AddSingleton<PlcRuntime>();
        services.AddHostedService(sp => sp.GetRequiredService<PlcRuntime>());

        // View model + main window.
        services.AddSingleton(sp => new MainViewModel(
            faults.ToDictionary(f => f.Code, f => f.Message)));
        services.AddSingleton<MainWindow>();
    }

    /// <summary>
    /// The writable engineering parameters (D201/D202/D204/D205, design §4.3). Ranges are the
    /// configured allowed limits; until they are sourced from config they use repository defaults.
    /// Only parameters with a valid configured range are writable (ParameterService rejects otherwise).
    /// </summary>
    private static IEnumerable<ParameterDefinition> BuildWritableParameters()
        => new[]
        {
            new ParameterDefinition { Name = "D201", Address = 101, Unit = "Hz", Min = 0, Max = 1000 },    // 调宽速度
            new ParameterDefinition { Name = "D202", Address = 102, Unit = "mm", Min = 0, Max = 3000 },    // 目标宽度
            new ParameterDefinition { Name = "D204", Address = 104, Unit = "脉冲/mm", Min = 0, Max = 10000 }, // 脉冲当量
            new ParameterDefinition { Name = "D205", Address = 105, Unit = "Hz", Min = 0, Max = 1000 },    // 皮带速度
        };

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
