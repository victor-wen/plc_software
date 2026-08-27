using Microsoft.Extensions.Hosting;
using PlcSoftware.Infrastructure.Simulation;

namespace PlcSoftware.App.Services;

/// <summary>
/// Wall-clock driver for the demo <see cref="SimulationScenarioRunner"/>. The in-memory simulation is static
/// unless something advances its virtual clock, so this hosted loop replays the default scenario on a real
/// cadence — <c>+250 ms</c> per tick (4 ticks/s) — which drives the D101 heartbeat, the automatic-flow step
/// pointer (D200 / D102 / M200-M205) and the production counters. Without it the demo showed a frozen
/// snapshot and the UI contradicted itself (在线 + 心跳丢失); with it the scenario stays alive until shutdown.
/// Fully in-memory; no transport, no wall-clock reads.
///
/// <para>Idempotent by construction: <see cref="SimulationScenarioRunner.Advance"/> applies each scenario
/// event at or before the new virtual time exactly once, so this loop can simply keep ticking and the
/// scenario replays deterministically.</para>
/// </summary>
internal sealed class SimulationScenarioDriver : BackgroundService
{
    /// <summary>The fixed demo cadence: one virtual step per tick (4 ticks/second).</summary>
    public static readonly TimeSpan Tick = TimeSpan.FromMilliseconds(250);

    private readonly SimulationScenarioRunner _runner;

    /// <summary>Builds the driver over the scenario runner it advances.</summary>
    public SimulationScenarioDriver(SimulationScenarioRunner runner)
    {
        _runner = runner ?? throw new ArgumentNullException(nameof(runner));
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(Tick, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                // Shutdown: join the loop cleanly.
                break;
            }

            _runner.Advance(Tick);
        }
    }
}
