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

        // Fast sub-keys are present in the merged snapshot.
        Assert.Equal((ushort)0x0042, snapshot.Values["D101"]);
        Assert.Equal((ushort)0x0003, snapshot.Values["D110"]);
        Assert.True((bool)snapshot.Values["M0"]!);

        // Process sub-keys are present in the same snapshot.
        Assert.Equal((ushort)7, snapshot.Values["D200"]);
        Assert.Equal(0xABCD1234u, (uint)snapshot.Values["D207.D208"]!);

        // Published exactly once, and the delivered snapshot is the same immutable one now current.
        Assert.Single(published);
        Assert.Same(snapshot, published[0]);
    }

    [Fact]
    public void Publish_EachCycle_IsAFreshDictionary_NoStaleKeyDuplication()
    {
        var store = new DeviceStateStore();
        var merger = new SnapshotMerger(store);

        // Cycle 1: the fast block reports D101 and M0; the process block reports D200.
        merger.Publish(
            new Dictionary<string, object?> { ["D101"] = (ushort)1, ["M0"] = true },
            new Dictionary<string, object?> { ["D200"] = (ushort)7 },
            Timestamp);

        // Cycle 2: M0 is no longer read, D101 and D200 change. The merge must start from a clean
        // dictionary each cycle — a stale M0 from cycle 1 must not survive.
        var second = merger.Publish(
            new Dictionary<string, object?> { ["D101"] = (ushort)2 },
            new Dictionary<string, object?> { ["D200"] = (ushort)8 },
            Timestamp);

        Assert.False(second.Values.ContainsKey("M0"));
        Assert.Equal((ushort)2, second.Values["D101"]);
        Assert.Equal((ushort)8, second.Values["D200"]);
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
            new Dictionary<string, object?> { ["D101"] = (ushort)1 }, empty, Timestamp);
        var second = merger.Publish(
            new Dictionary<string, object?> { ["D101"] = (ushort)2 }, empty, Timestamp);

        Assert.Equal(new[] { first, second }, published);
        Assert.Same(second, store.Current);
    }

    private static ushort[] FastRegisters()
    {
        var registers = new ushort[11];
        registers[0] = 0x0001;  // D100 bit0 → M0.
        registers[1] = 0x0042;  // D101 heartbeat.
        registers[10] = 0x0003; // D110 fault code.
        return registers;
    }

    private static ushort[] ProcessRegisters()
    {
        var registers = new ushort[14];
        registers[0] = 7;       // D200 step number.
        registers[7] = 0x1234;  // D207 low word.
        registers[8] = 0xABCD;  // D208 high word.
        return registers;
    }
}
