namespace PlcSoftware.Core.Services;

using PlcSoftware.Core.Abstractions;
using PlcSoftware.Core.Models;

/// <summary>
/// Coordinator helper that combines the fast and process <see cref="RegisterDecoder"/> sub-dictionaries
/// into a single coherent <see cref="DeviceSnapshot"/> and publishes it atomically through
/// <see cref="IDeviceStateStore"/> (Review Gate 4).
///
/// <para><b>Fresh dictionary per cycle.</b> Each <see cref="Publish"/> builds a brand-new merged
/// dictionary from the supplied sub-key dictionaries, so a stale key from a previous cycle can never
/// leak into the current snapshot. Process sub-keys overwrite any overlapping fast sub-key (there are
/// none today, but the merge is order-correct regardless).</para>
///
/// <para><b>Single atomic publish.</b> The merge completes before <see cref="IDeviceStateStore.Publish"/>
/// is called once, so <see cref="IDeviceStateStore.SnapshotChanged"/> fires exactly once per cycle with a
/// fully-constructed, immutable snapshot — never a partially built or mixed fast/process value.</para>
/// </summary>
public sealed class SnapshotMerger
{
    private readonly IDeviceStateStore _store;

    /// <summary>Builds the merger over the atomic state store.</summary>
    public SnapshotMerger(IDeviceStateStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    /// <summary>
    /// Merges <paramref name="fast"/> and <paramref name="process"/> into one fresh snapshot and
    /// publishes it once. Returns the published snapshot.
    /// </summary>
    public DeviceSnapshot Publish(
        IReadOnlyDictionary<string, object?> fast,
        IReadOnlyDictionary<string, object?> process,
        DateTime timestamp)
    {
        if (fast is null)
        {
            throw new ArgumentNullException(nameof(fast));
        }

        if (process is null)
        {
            throw new ArgumentNullException(nameof(process));
        }

        // ONE fresh dictionary per cycle: process values overwrite any overlapping fast sub-key, and no
        // stale key from a previous cycle can survive because the dictionary is built from scratch.
        var merged = new Dictionary<string, object?>(fast);
        foreach (var (key, value) in process)
        {
            merged[key] = value;
        }

        var snapshot = new DeviceSnapshot(merged, timestamp);
        _store.Publish(snapshot);
        return snapshot;
    }
}
