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
    public int Min { get; set; }
    public int Max { get; set; }

    /// <summary>Returns a list of validation errors, empty when the definition is valid.</summary>
    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (Min > Max)
        {
            errors.Add("min must be less than or equal to max.");
        }

        return errors;
    }
}
