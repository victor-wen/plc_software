namespace PlcSoftware.Core.Models;

/// <summary>
/// Immutable snapshot of decoded PLC point values at a point in time.
/// Keys are point names or logical addresses; values are the decoded primitives.
/// </summary>
public sealed class DeviceSnapshot
{
    public DeviceSnapshot(IReadOnlyDictionary<string, object?> values, DateTime timestamp)
    {
        Values = values;
        Timestamp = timestamp;
    }

    public IReadOnlyDictionary<string, object?> Values { get; }
    public DateTime Timestamp { get; }
}
