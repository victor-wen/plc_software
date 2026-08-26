namespace PlcSoftware.Core.Configuration;

/// <summary>
/// Parity for a serial link. Mirrors System.IO.Ports semantics but is defined here
/// so Core stays free of a SerialPort dependency.
/// </summary>
public enum Parity
{
    None = 0,
    Odd = 1,
    Even = 2,
    Mark = 3,
    Space = 4,
}

/// <summary>
/// Stop bits for a serial link. Mirrors System.IO.Ports semantics but is defined here
/// so Core stays free of a SerialPort dependency.
/// </summary>
public enum StopBits
{
    None = 0,
    One = 1,
    Two = 2,
    OnePointFive = 3,
}

/// <summary>
/// Modbus RTU serial link settings, validated at load time.
/// </summary>
public sealed class SerialConnectionOptions
{
    public string PortName { get; set; } = "COM1";
    public int BaudRate { get; set; } = 9600;
    public int DataBits { get; set; } = 8;
    public Parity Parity { get; set; } = Parity.None;
    public StopBits StopBits { get; set; } = StopBits.One;

    /// <summary>Modbus RTU slave / station address (1-247).</summary>
    public byte SlaveId { get; set; } = 1;

    /// <summary>Per-request timeout in milliseconds. Must be non-negative.</summary>
    public int TimeoutMs { get; set; } = 1000;

    public int Retries { get; set; } = 3;

    /// <summary>Returns a list of validation errors, empty when the configuration is valid.</summary>
    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (SlaveId < 1 || SlaveId > 247)
        {
            errors.Add("slaveId must be between 1 and 247 (Modbus RTU range).");
        }

        if (TimeoutMs < 0)
        {
            errors.Add("timeout must be non-negative.");
        }

        return errors;
    }
}
