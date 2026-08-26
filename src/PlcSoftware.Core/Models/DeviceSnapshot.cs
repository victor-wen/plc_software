using System.Collections.ObjectModel;

namespace PlcSoftware.Core.Models;

/// <summary>
/// Immutable snapshot of decoded PLC point values at a point in time.
/// Keys are point names or logical addresses; values are the decoded primitives.
/// </summary>
public sealed class DeviceSnapshot
{
    public DeviceSnapshot(IReadOnlyDictionary<string, object?> values, DateTime timestamp)
    {
        if (values is null)
        {
            throw new ArgumentNullException(nameof(values));
        }

        // Defensively copy the caller's dictionary so a later mutation of the source cannot
        // retroactively alter this value. The copy is exposed read-only, which keeps the
        // atomic-publish guarantee: a published snapshot is immutable from the moment it is built.
        Values = new ReadOnlyDictionary<string, object?>(
            new Dictionary<string, object?>(values));
        Timestamp = timestamp;
    }

    public IReadOnlyDictionary<string, object?> Values { get; }
    public DateTime Timestamp { get; }
}
