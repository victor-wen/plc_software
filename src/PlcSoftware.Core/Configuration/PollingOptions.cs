namespace PlcSoftware.Core.Configuration;

/// <summary>
/// Modbus polling cadence. Intervals are in milliseconds and must be positive.
/// </summary>
public sealed class PollingOptions
{
    /// <summary>Fast group (D100-D110 heartbeat/status block).</summary>
    public int FastIntervalMs { get; set; } = 250;

    /// <summary>Process group (D200-D213 process/parameter block).</summary>
    public int ProcessIntervalMs { get; set; } = 500;

    /// <summary>I/O diagnostics group (X/Y area).</summary>
    public int DiagnosticsIntervalMs { get; set; } = 500;

    /// <summary>Returns a list of validation errors, empty when the configuration is valid.</summary>
    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (FastIntervalMs <= 0)
        {
            errors.Add("fast interval must be positive.");
        }

        if (ProcessIntervalMs <= 0)
        {
            errors.Add("process interval must be positive.");
        }

        if (DiagnosticsIntervalMs <= 0)
        {
            errors.Add("diagnostics interval must be positive.");
        }

        return errors;
    }
}
