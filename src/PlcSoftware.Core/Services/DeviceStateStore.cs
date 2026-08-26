namespace PlcSoftware.Core.Services;

using PlcSoftware.Core.Abstractions;
using PlcSoftware.Core.Models;

/// <summary>
/// Thread-safe holder of the latest <see cref="DeviceSnapshot"/> behind the
/// <see cref="IDeviceStateStore"/> abstraction.
///
/// <para><b>Atomic publish.</b> <see cref="Publish"/> swaps <see cref="Current"/> and then raises
/// <see cref="IDeviceStateStore.SnapshotChanged"/> exactly once. A concurrent reader of
/// <see cref="Current"/> observes either the previous or the new snapshot — never a partially constructed
/// value — because the snapshot is built fully before it is published and only the immutable reference is
/// swapped (under the lock).</para>
///
/// <para><b>Merge responsibility (Review Gate 4).</b> The store publishes whole snapshots only. It does
/// not merge the per-group <see cref="RegisterDecoder"/> output itself; coordinator code assembles a
/// coherent snapshot from the fast/process decodes and passes it to <see cref="Publish"/>. This keeps
/// the store a single-responsibility, atomic publisher.</para>
/// </summary>
public sealed class DeviceStateStore : IDeviceStateStore
{
    private readonly object _sync = new();
    private DeviceSnapshot _current;

    /// <summary>Builds a store beginning on an empty snapshot.</summary>
    public DeviceStateStore()
    {
        _current = new DeviceSnapshot(new Dictionary<string, object?>(), DateTime.MinValue);
    }

    /// <inheritdoc />
    public DeviceSnapshot Current
    {
        get
        {
            lock (_sync)
            {
                return _current;
            }
        }
    }

    /// <inheritdoc />
    public event EventHandler<DeviceSnapshot>? SnapshotChanged;

    /// <inheritdoc />
    public void Publish(DeviceSnapshot snapshot)
    {
        if (snapshot is null)
        {
            throw new ArgumentNullException(nameof(snapshot));
        }

        // Swap the immutable reference under the lock, then raise outside it: a subscriber always sees
        // the new snapshot through <see cref="Current"/>, and the event fires once per publish.
        DeviceSnapshot raised;
        lock (_sync)
        {
            _current = snapshot;
            raised = snapshot;
        }

        SnapshotChanged?.Invoke(this, raised);
    }
}
