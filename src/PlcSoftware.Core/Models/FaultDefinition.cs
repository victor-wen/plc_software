namespace PlcSoftware.Core.Models;

/// <summary>
/// Maps a PLC fault code (D110, K1-K7) to its display message. Code 0 means no fault.
/// </summary>
public sealed class FaultDefinition
{
    public int Code { get; set; }
    public string Message { get; set; } = string.Empty;
}
