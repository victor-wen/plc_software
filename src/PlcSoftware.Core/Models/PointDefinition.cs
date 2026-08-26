namespace PlcSoftware.Core.Models;

/// <summary>
/// Describes a single PLC point (X/Y/M/D) as referenced by the point map and decoders.
/// </summary>
public sealed class PointDefinition
{
    /// <summary>Chinese display name, e.g. "急停按钮".</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Logical address in original notation, e.g. "X0", "D100".</summary>
    public string Address { get; set; } = string.Empty;

    /// <summary>Zero-based Modbus protocol address.</summary>
    public ushort ProtocolAddress { get; set; }

    /// <summary>True when the point may be written by the host.</summary>
    public bool IsWritable { get; set; }
}
