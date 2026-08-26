namespace PlcSoftware.Core.Services;

using PlcSoftware.Core.Models;

/// <summary>
/// Strict validator for a PLC point map. It rejects duplicate logical addresses, duplicate
/// protocol addresses, illegal bit-index notation and point maps that are missing the
/// host-project-added (PLC 新增) data registers D105, D106 and D213.
///
/// Logical addresses are compared case-insensitively so that e.g. "x0" and "X0" are not
/// silently both accepted, while the original address text is never rewritten.
/// </summary>
public static class PointMapValidator
{
    /// <summary>Highest valid bit index in a 16-bit data register.</summary>
    public const int MaxBitIndex = 15;

    /// <summary>
    /// Data registers the supervisory control depends on. D106 is written by the host and
    /// watched by the PLC; D105.bit0 and D213 are host-project additions ("PLC 新增").
    /// </summary>
    private static readonly string[] RequiredAddresses = { "D105.bit0", "D106", "D213" };

    /// <summary>Returns a list of validation errors, empty when the point map is valid.</summary>
    public static IReadOnlyList<string> Validate(IReadOnlyList<PointDefinition> points)
    {
        var errors = new List<string>();
        if (points is null)
        {
            errors.Add("point map must not be null.");
            return errors;
        }

        var seenLogical = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // Zero-based protocol addressing is scoped per Modbus register area (X/Y/M/D).
        var seenProtocol = new Dictionary<(char Area, ushort Address), string>();

        foreach (var point in points)
        {
            if (point is null)
            {
                errors.Add("point map must not contain null entries.");
                continue;
            }

            var address = point.Address;
            if (string.IsNullOrWhiteSpace(address))
            {
                errors.Add("point address must not be empty.");
            }
            else
            {
                ValidateBitIndex(address, errors);
            }

            if (address is not null && !seenLogical.Add(address))
            {
                errors.Add($@"Duplicate logical address '{address}'.");
            }

            if (address is not null && address.Length > 0)
            {
                var area = char.ToUpperInvariant(address[0]);
                var key = (area, point.ProtocolAddress);
                if (seenProtocol.TryGetValue(key, out var existing))
                {
                    errors.Add($@"Duplicate protocol address {point.ProtocolAddress} in area '{area}' at '{address}' (already used by '{existing}').");
                }
                else
                {
                    seenProtocol[key] = address;
                }
            }
        }

        var present = new HashSet<string>(
            points.Where(p => p is not null).Select(p => p.Address).Where(a => a is not null),
            StringComparer.OrdinalIgnoreCase);
        foreach (var required in RequiredAddresses)
        {
            if (!present.Contains(required))
            {
                errors.Add($@"Missing required point '{required}'.");
            }
        }

        return errors;
    }

    private static void ValidateBitIndex(string address, IList<string> errors)
    {
        var marker = address.IndexOf(".bit", StringComparison.OrdinalIgnoreCase);
        if (marker < 0)
        {
            return;
        }

        var token = address[(marker + 4)..];
        if (!int.TryParse(token, out var bitIndex) || bitIndex < 0 || bitIndex > MaxBitIndex)
        {
            errors.Add($@"Illegal bit index '{token}' in address '{address}' (expected 0..{MaxBitIndex}).");
        }
    }
}
