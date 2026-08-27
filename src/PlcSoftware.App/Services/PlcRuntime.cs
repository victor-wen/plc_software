using Microsoft.Extensions.Hosting;
using PlcSoftware.Core.Abstractions;
using PlcSoftware.Core.Models;
using PlcSoftware.Core.Services;

namespace PlcSoftware.App.Services;

/// <summary>
/// The application's background runtime: starts and joins the three Core loops (connection supervision,
/// polling, D106 host watchdog) and observes the published snapshot to drive the heartbeat monitor and
/// the alarm service. It is registered as one of the app's <c>IHostedService</c>s (alongside the
/// <see cref="SimulationScenarioDriver"/>), so the Generic Host starts them on <c>StartAsync</c> and joins
/// them on <c>StopAsync</c>.
///
/// <para>Snapshot fan-out is single-writer: <see cref="SnapshotCoordinator"/> is the only publisher, so
/// every observe callback here runs on the one polling loop thread.</para>
/// </summary>
internal sealed class PlcRuntime : BackgroundService
{
    private readonly ConnectionSupervisor _supervisor;
    private readonly PollingService _polling;
    private readonly HmiWatchdogService _watchdog;
    private readonly SnapshotCoordinator _coordinator;
    private readonly HeartbeatMonitor _heartbeat;
    private readonly AlarmService _alarm;
    private readonly IDeviceStateStore _store;

    public PlcRuntime(
        ConnectionSupervisor supervisor,
        PollingService polling,
        HmiWatchdogService watchdog,
        SnapshotCoordinator coordinator,
        HeartbeatMonitor heartbeat,
        AlarmService alarm,
        IDeviceStateStore store)
    {
        _supervisor = supervisor ?? throw new ArgumentNullException(nameof(supervisor));
        _polling = polling ?? throw new ArgumentNullException(nameof(polling));
        _watchdog = watchdog ?? throw new ArgumentNullException(nameof(watchdog));
        _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
        _heartbeat = heartbeat ?? throw new ArgumentNullException(nameof(heartbeat));
        _alarm = alarm ?? throw new ArgumentNullException(nameof(alarm));
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Observe every published snapshot (runs on the single polling loop thread).
        _store.SnapshotChanged += OnSnapshot;

        try
        {
            // The three loops share the host lifetime token so shutdown joins them cleanly.
            var loops = new[]
            {
                _supervisor.RunAsync(stoppingToken),
                _polling.RunAsync(stoppingToken),
                _watchdog.RunAsync(stoppingToken),
            };

            await Task.WhenAll(loops);
        }
        finally
        {
            _store.SnapshotChanged -= OnSnapshot;
        }
    }

    private void OnSnapshot(object? sender, DeviceSnapshot snapshot)
    {
        var values = snapshot.Values;

        // D101 heartbeat counter → heartbeat monitor (any different value proves the PLC is alive).
        if (values.TryGetValue("D101", out var heartbeat) && heartbeat is ushort d101)
        {
            _heartbeat.Observe(d101);
        }

        // D110 fault code → alarm service (0 = no fault).
        if (values.TryGetValue("D110", out var fault) && fault is ushort d110)
        {
            _alarm.Observe(d110);
        }
    }
}
