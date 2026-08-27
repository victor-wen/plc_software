namespace PlcSoftware.Core.Configuration;

using System.Text.Json;
using System.Text.Json.Serialization;

/// <summary>
/// Thrown when <c>config/ui-layout.json</c> is missing, malformed or fails validation; the message lists
/// every problem found (the file is the single source of the whole operator screen, so a broken layout is
/// a startup error rather than a silently degraded UI).
/// </summary>
public sealed class UiLayoutValidationException : Exception
{
    public UiLayoutValidationException(string message)
        : base(message)
    {
    }

    public UiLayoutValidationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Loads and validates the configurable HMI layout (<c>UiLayoutDefinition</c>) from a JSON document
/// (typically <c>config/ui-layout.json</c>, copied next to the binaries — see <c>docs/configuration.md</c>).
///
/// <para><b>Strictness.</b> Unknown properties are ignored (forward compatibility), but unknown enum
/// values, missing required fields and dangling page references fail validation; the loader throws
/// <see cref="UiLayoutValidationException"/> with the full error list. A layout that is missing entirely
/// (null file) yields <c>null</c> from <see cref="TryLoad"/> so the shell can fall back to the legacy
/// hand-written navigation.</para>
/// </summary>
public static class UiLayoutLoader
{
    /// <summary>The camelCase + enum-as-string options used for ui-layout.json.</summary>
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false) },
    };

    /// <summary>Loads and validates the layout from a JSON string. Throws <see cref="UiLayoutValidationException"/>
    /// on malformed JSON or validation errors.</summary>
    public static UiLayoutDefinition Load(string json)
    {
        if (json is null)
        {
            throw new ArgumentNullException(nameof(json));
        }

        UiLayoutDefinition layout;
        try
        {
            layout = JsonSerializer.Deserialize<UiLayoutDefinition>(json, Options)
                ?? throw new JsonException("ui-layout.json is 'null'.");
        }
        catch (JsonException ex)
        {
            throw new UiLayoutValidationException($"ui-layout.json is not valid JSON: {ex.Message}", ex);
        }

        var errors = layout.Validate();
        if (errors.Count > 0)
        {
            throw new UiLayoutValidationException(
                $"ui-layout.json failed validation:{Environment.NewLine}  " + string.Join(Environment.NewLine + "  ", errors));
        }

        return layout;
    }

    /// <summary>Reads and loads <paramref name="path"/> (UTF-8). Throws <see cref="UiLayoutValidationException"/> on failure.</summary>
    public static UiLayoutDefinition LoadFromFile(string path)
    {
        if (path is null)
        {
            throw new ArgumentNullException(nameof(path));
        }

        if (!File.Exists(path))
        {
            throw new UiLayoutValidationException($"ui-layout file '{path}' does not exist.");
        }

        return Load(File.ReadAllText(path));
    }

    /// <summary>Loads the layout, or returns null when the file does not exist (legacy non-configurable shell).
    /// Still throws on malformed JSON / validation errors.</summary>
    public static UiLayoutDefinition? TryLoadFromFile(string path)
        => File.Exists(path) ? LoadFromFile(path) : null;
}
