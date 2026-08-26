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

    /// <summary>
    /// Atomically replaces <see cref="Current"/> and raises <see cref="SnapshotChanged"/> once.
    ///
    /// <para><b>Single-writer contract.</b> Publish is owned by exactly one coordinator loop (the
    /// polling/supervision loop, via <see cref="SnapshotMerger"/>). Concurrent publishes from multiple
    /// callers are <em>not</em> supported: <see cref="SnapshotChanged"/> is raised outside the internal
    /// lock after <see cref="Current"/> is swapped, so two concurrently-interleaved publishes could
    /// invert the event order relative to <see cref="Current"/>. The coordinator is therefore the sole
    /// publisher, and its own loop already serializes every cycle.</para>
    /// </summary>
    void Publish(DeviceSnapshot snapshot);
}
