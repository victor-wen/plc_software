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
        if (string.IsNullOrWhiteSpace(PortName))
        {
            errors.Add("portName must not be empty.");
        }

        if (BaudRate <= 0)
        {
            errors.Add("baudRate must be greater than zero.");
        }

        if (DataBits is not (5 or 6 or 7 or 8))
        {
            errors.Add("dataBits must be one of 5, 6, 7 or 8.");
        }

        // Reject undefined enum values and StopBits.None: None is not a real serial stop-bits
        // setting, so it must surface here at load time rather than throwing from
        // System.IO.Ports.SerialPort at construction.
        if (Parity is not (Parity.None or Parity.Odd or Parity.Even or Parity.Mark or Parity.Space))
        {
            errors.Add("parity must be one of None, Odd, Even, Mark or Space.");
        }

        if (StopBits is not (StopBits.One or StopBits.Two or StopBits.OnePointFive))
        {
            errors.Add("stopBits must be one of One, Two or OnePointFive (None is not a valid value).");
        }

        if (SlaveId < 1 || SlaveId > 247)
        {
            errors.Add("slaveId must be between 1 and 247 (Modbus RTU range).");
        }

        if (TimeoutMs < 0)
        {
            errors.Add("timeout must be non-negative.");
        }

        if (Retries < 0)
        {
            errors.Add("retries must be non-negative.");
        }

        return errors;
    }
}
