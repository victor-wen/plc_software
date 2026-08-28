using PlcSoftware.Core.Abstractions;
using PlcSoftware.Core.Models;
using PlcSoftware.Core.Services;

namespace PlcSoftware.Core.Tests.Services;

/// <summary>
/// Behavioural tests for <see cref="DeviceStateStore"/> (the <see cref="IDeviceStateStore"/>
/// implementation).
///
/// Verified rules:
///   - the store starts on an empty snapshot and <see cref="IDeviceStateStore.Publish"/> replaces
///     <see cref="IDeviceStateStore.Current"/> with the published snapshot;
///   - <see cref="IDeviceStateStore.SnapshotChanged"/> fires exactly once per publish, in order, with
///     the same (immutable) snapshot that was published;
///   - publishing is atomic: a concurrent reader of <see cref="IDeviceStateStore.Current"/> never
///     observes a partially-constructed snapshot — only a fully-built, previously-published reference.
/// </summary>
public class DeviceStateStoreTests
{
    [Fact]
    public void Initial_IsAnEmptySnapshot()
    {
        var store = new DeviceStateStore();

        var initial = store.Current;
        Assert.NotNull(initial);
        Assert.NotNull(initial.Values);
        Assert.Empty(initial.Values);
    }

    [Fact]
    public void Publish_SetsCurrent_ToThePublishedSnapshot()
    {
        var store = new DeviceStateStore();
        var snapshot = new DeviceSnapshot(
            new Dictionary<string, object?> { ["D140"] = (ushort)1 },
            DateTime.UtcNow);

        store.Publish(snapshot);

        Assert.Same(snapshot, store.Current);
        Assert.Equal((ushort)1, store.Current.Values["D140"]);
    }

    [Fact]
    public void Publish_FiresSnapshotChanged_OncePerPublish_WithTheNewSnapshot()
    {
        var store = new DeviceStateStore();
        var fired = new List<DeviceSnapshot>();
        store.SnapshotChanged += (_, s) => fired.Add(s);

        var first = new DeviceSnapshot(new Dictionary<string, object?> { ["v"] = 1 }, DateTime.UtcNow);
        var second = new DeviceSnapshot(new Dictionary<string, object?> { ["v"] = 2 }, DateTime.UtcNow);

        store.Publish(first);
        store.Publish(second);

        Assert.Equal(new[] { first, second }, fired); // once each, in publish order, same references.
        Assert.Same(second, store.Current);
    }

    [Fact]
    public void Publish_NullSnapshot_ThrowsArgumentNull()
    {
        var store = new DeviceStateStore();
        Assert.Throws<ArgumentNullException>(() => store.Publish(null!));
    }

    [Fact]
    public void Publish_DefensivelyFreezesValues_MutationOfSourceDoesNotAlterPublishedSnapshot()
    {
        var store = new DeviceStateStore();
        var source = new Dictionary<string, object?> { ["D140"] = (ushort)1 };
        var snapshot = new DeviceSnapshot(source, DateTime.UtcNow);

        DeviceSnapshot? delivered = null;
        store.SnapshotChanged += (_, s) => delivered = s;

        store.Publish(snapshot);

        // Mutate the caller's dictionary after publish. A published snapshot must be immutable,
        // otherwise these mutations would retroactively alter Current and the event-delivered snapshot.
        source["D140"] = (ushort)999;
        source["added"] = "later";
        source.Remove("D140");

        Assert.Same(snapshot, store.Current);
        Assert.Equal((ushort)1, store.Current.Values["D140"]);
        Assert.False(store.Current.Values.ContainsKey("added"));

        Assert.NotNull(delivered);
        Assert.Equal((ushort)1, delivered.Values["D140"]);
        Assert.False(delivered.Values.ContainsKey("added"));
    }

    [Fact]
    public async Task Publish_IsAtomic_CurrentNeverExposesAPartialSnapshot()
    {
        var store = new DeviceStateStore();

        // A snapshot is valid once it becomes Current: the initial empty snapshot and every one we
        // publish. We record each snapshot before publishing it, so any snapshot a reader observes
        // (which can only be one already swapped into Current) is guaranteed to be in this set.
        var valid = new System.Collections.Concurrent.ConcurrentDictionary<DeviceSnapshot, byte>();
        valid[store.Current] = 0;

        using var cts = new CancellationTokenSource();
        var observed = new System.Collections.Concurrent.ConcurrentQueue<DeviceSnapshot>();
        var firstShow = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var reader = Task.Run(() =>
        {
            while (!cts.IsCancellationRequested)
            {
                observed.Enqueue(store.Current);
                firstShow.TrySetResult();
                Thread.Yield();
            }
        });

        for (var i = 0; i < 2000; i++)
        {
            var snapshot = new DeviceSnapshot(
                new Dictionary<string, object?> { ["seq"] = i, ["pad"] = new byte[64] },
                DateTime.UtcNow.AddTicks(i));
            valid[snapshot] = 0;
            store.Publish(snapshot);
        }

        // Wait until the reader thread has observed at least one snapshot, so the emptiness assertion
        // below cannot flake on a schedule where the reader never ran before cancellation.
        await firstShow.Task.WaitAsync(TimeSpan.FromSeconds(5));

        cts.Cancel();
        await reader;

        // Every observed snapshot is a fully built, previously-published reference — Current is never torn.
        Assert.NotEmpty(observed);
        Assert.All(observed, s => Assert.True(valid.ContainsKey(s)));
    }
}
