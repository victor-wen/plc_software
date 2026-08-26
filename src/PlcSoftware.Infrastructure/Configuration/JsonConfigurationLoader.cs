using System.Text.Json;
using System.Text.Json.Serialization;
using PlcSoftware.Core.Configuration;
using PlcSoftware.Core.Models;

namespace PlcSoftware.Infrastructure.Configuration;

/// <summary>
/// Loads the repository's JSON configuration files (appsettings.json, faults.json and
/// point-map.simulation.json) into the typed Core option/models. Uses System.Text.Json so
/// Infrastructure stays free of a provider-specific configuration stack; all files are
/// deserialized with case-insensitive property matching.
/// </summary>
public sealed class JsonConfigurationLoader
{
    private readonly JsonSerializerOptions _options;

    public JsonConfigurationLoader()
    {
        _options = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            AllowTrailingCommas = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
        };
        _options.Converters.Add(new JsonStringEnumConverter());
    }

    public SerialConnectionOptions LoadSerialOptions(string path)
    {
        var dto = ReadAppSettings(path);
        return dto.Serial ?? throw new InvalidDataException("appsettings.json is missing the 'serial' section.");
    }

    public PollingOptions LoadPollingOptions(string path)
    {
        var dto = ReadAppSettings(path);
        return dto.Polling ?? throw new InvalidDataException("appsettings.json is missing the 'polling' section.");
    }

    public HistoryOptions LoadHistoryOptions(string path)
    {
        var dto = ReadAppSettings(path);
        return dto.History ?? throw new InvalidDataException("appsettings.json is missing the 'history' section.");
    }

    public IReadOnlyList<FaultDefinition> LoadFaults(string path)
    {
        using var stream = File.OpenRead(path);
        return JsonSerializer.Deserialize<List<FaultDefinition>>(stream, _options)
               ?? new List<FaultDefinition>();
    }

    public IReadOnlyList<PointDefinition> LoadPointMap(string path)
    {
        using var stream = File.OpenRead(path);
        return JsonSerializer.Deserialize<List<PointDefinition>>(stream, _options)
               ?? new List<PointDefinition>();
    }

    private AppSettingsDto ReadAppSettings(string path)
    {
        using var stream = File.OpenRead(path);
        return JsonSerializer.Deserialize<AppSettingsDto>(stream, _options)
               ?? new AppSettingsDto();
    }

    private sealed class AppSettingsDto
    {
        public SerialConnectionOptions? Serial { get; set; }
        public PollingOptions? Polling { get; set; }
        public HistoryOptions? History { get; set; }
    }
}
