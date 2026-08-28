using PlcSoftware.Core.Abstractions;
using PlcSoftware.Core.Models;
using PlcSoftware.Core.Services;

namespace PlcSoftware.Core.Tests.Services;

/// <summary>
/// Behavioural tests for <see cref="SnapshotMerger"/> — the coordinator helper that combines the fast
/// and process <see cref="RegisterDecoder"/> sub-keys into ONE fresh snapshot and publishes it
/// atomically (Review Gate 4 carry-over).
///
/// Verified rules:
///   - fast sub-keys and process sub-keys are combined into a single snapshot, published exactly once;
///   - each cycle builds a fresh dictionary, so a stale key from a previous cycle never leaks in;
///   - publishes are serialized: each publish fires <see cref="IDeviceStateStore.SnapshotChanged"/> once,
///     in order, and <see cref="IDeviceStateStore.Current"/> is the latest published snapshot.
/// </summary>
public class SnapshotMergerTests
{
    private static readonly DateTime Timestamp =
        new(2026, 8, 26, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Publish_CombinesFastAndProcessSubKeys_IntoOneSnapshot_PublishedOnce()
    {
        var store = new DeviceStateStore();
        var published = new List<DeviceSnapshot>();
        store.SnapshotChanged += (_, s) => published.Add(s);
        var merger = new SnapshotMerger(store);

        var fast = RegisterDecoder.DecodeFast(FastRegisters());
        var process = RegisterDecoder.DecodeProcess(ProcessRegisters());

        merger.Publish(fast, process, Timestamp);

        var snapshot = store.Current;
        Assert.NotNull(snapshot);

        // Fast sub-keys are present in the merged snapshot (M0 via D100, fault code D110).
        Assert.Equal((ushort)0x0003, snapshot.Values["D110"]);
        Assert.True((bool)snapshot.Values["M0"]!);

        // Process sub-keys are present in the same snapshot — heartbeat D140, step D120,
        // width pulse D136 and production D138 (all single-word).
        Assert.Equal((ushort)0x0042, snapshot.Values["D140"]);
        Assert.Equal((ushort)7, snapshot.Values["D120"]);
        Assert.Equal((ushort)0xABCD, snapshot.Values["D136"]);
        Assert.Equal((ushort)0x1234, snapshot.Values["D138"]);

        // Published exactly once, and the delivered snapshot is the same immutable one now current.
        Assert.Single(published);
        Assert.Same(snapshot, published[0]);
    }

    [Fact]
    public void Publish_EachCycle_IsAFreshDictionary_NoStaleKeyDuplication()
    {
        var store = new DeviceStateStore();
        var merger = new SnapshotMerger(store);

        // Cycle 1: the fast block reports D140 and M0; the process block reports D120.
        merger.Publish(
            new Dictionary<string, object?> { ["D140"] = (ushort)1, ["M0"] = true },
            new Dictionary<string, object?> { ["D120"] = (ushort)7 },
            Timestamp);

        // Cycle 2: M0 is no longer read, D140 and D120 change. The merge must start from a clean
        // dictionary each cycle — a stale M0 from cycle 1 must not survive.
        var second = merger.Publish(
            new Dictionary<string, object?> { ["D140"] = (ushort)2 },
            new Dictionary<string, object?> { ["D120"] = (ushort)8 },
            Timestamp);

        Assert.False(second.Values.ContainsKey("M0"));
        Assert.Equal((ushort)2, second.Values["D140"]);
        Assert.Equal((ushort)8, second.Values["D120"]);
    }

    [Fact]
    public void Publish_SerializesSnapshots_InOrder()
    {
        var store = new DeviceStateStore();
        var published = new List<DeviceSnapshot>();
        store.SnapshotChanged += (_, s) => published.Add(s);
        var merger = new SnapshotMerger(store);
        var empty = new Dictionary<string, object?>();

        var first = merger.Publish(
            new Dictionary<string, object?> { ["D140"] = (ushort)1 }, empty, Timestamp);
        var second = merger.Publish(
            new Dictionary<string, object?> { ["D140"] = (ushort)2 }, empty, Timestamp);

        Assert.Equal(new[] { first, second }, published);
        Assert.Same(second, store.Current);
    }

    private static ushort[] FastRegisters()
    {
        var registers = new ushort[11];
        registers[0] = 0x0001;  // D100 bit0 → M0.
        registers[10] = 0x0003; // D110 fault code.
        return registers;
    }

    private static ushort[] ProcessRegisters()
    {
        var registers = new ushort[21];
        registers[0] = 7;       // D120 step number.
        registers[16] = 0xABCD; // D136 width pulse single.
        registers[18] = 0x1234; // D138 production single.
        registers[20] = 0x0042; // D140 heartbeat.
        return registers;
    }
}
