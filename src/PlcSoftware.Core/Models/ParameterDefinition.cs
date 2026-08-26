namespace PlcSoftware.Core.Models;

/// <summary>
/// Describes an engineering parameter (e.g. D201, D202, D204, D205) with its
/// allowed value range. A parameter is writable only when the range is valid.
/// </summary>
public sealed class ParameterDefinition
{
    public string Name { get; set; } = string.Empty;
    public ushort Address { get; set; }
    public string Unit { get; set; } = string.Empty;

    /// <summary>
    /// Lower bound of the allowed value range. <c>null</c> means the bound is not
    /// configured yet, in which case validation fails (parameter must not be written).
    /// </summary>
    public int? Min { get; set; }

    /// <summary>
    /// Upper bound of the allowed value range. <c>null</c> means the bound is not
    /// configured yet, in which case validation fails (parameter must not be written).
    /// </summary>
    public int? Max { get; set; }

    /// <summary>Returns a list of validation errors, empty when the definition is valid.</summary>
    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (Min is null || Max is null)
        {
            errors.Add("min and max must both be configured before the parameter can be written.");
            return errors;
        }

        if (Min > Max)
        {
            errors.Add("min must be less than or equal to max.");
        }

        return errors;
    }
}
