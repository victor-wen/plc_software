namespace PlcSoftware.Core.Configuration;

/// <summary>
/// Local history retention settings.
/// </summary>
public sealed class HistoryOptions
{
    /// <summary>How many days history is retained. Must be positive.</summary>
    public int RetentionDays { get; set; } = 365;

    /// <summary>Returns a list of validation errors, empty when the configuration is valid.</summary>
    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (RetentionDays <= 0)
        {
            errors.Add("retention must be positive.");
        }

        return errors;
    }
}
