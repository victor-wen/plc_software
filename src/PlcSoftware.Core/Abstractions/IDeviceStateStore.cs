namespace PlcSoftware.Core.Abstractions;

using PlcSoftware.Core.Models;

/// <summary>
/// Holds the latest decoded PLC state and announces every new value.
/// </summary>
public interface IDeviceStateStore
{
    /// <summary>The latest snapshot, or an empty snapshot before the first publish.</summary>
    DeviceSnapshot Current { get; }

    /// <summary>Raised once for every <see cref="Publish"/> with the published snapshot.</summary>
    event EventHandler<DeviceSnapshot>? SnapshotChanged;

    /// <summary>Atomically replaces <see cref="Current"/> and raises <see cref="SnapshotChanged"/>.</summary>
    void Publish(DeviceSnapshot snapshot);
}
