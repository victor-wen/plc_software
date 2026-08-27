using System.IO;
using System.Text.Json;
using PlcSoftware.Core.Configuration;

namespace PlcSoftware.App.Services;

/// <summary>
/// Persists the operator-edited dashboard tiles (design §7 磁贴看板) so the runtime edits survive a
/// restart without touching the repository-owned <c>ui-layout.json</c>. The store is a seam for tests;
/// the production implementation is <see cref="JsonTileStore"/>.
/// </summary>
public interface ITileStore
{
    /// <summary>Loads the saved tiles of a page, or null when the page has no saved edits (use the layout defaults).</summary>
    List<UiTileDefinition>? Load(string pageId);

    /// <summary>Persists the tiles of a page (replace any previous saved set).</summary>
    void Save(string pageId, IReadOnlyList<UiTileDefinition> tiles);

    /// <summary>Removes the saved tiles of a page (restores the layout defaults).</summary>
    void Clear(string pageId);
}

/// <summary>
/// <see cref="ITileStore"/> over <c>config/dashboard.tiles.json</c> (next to the binaries, in the
/// user-editable config folder): <c>{ "pages": { "home": [ {tile}, … ] } }</c>. A missing file means
/// "no edits yet". Uses the same camelCase + enum-as-string options as <see cref="UiLayoutLoader"/>.
/// </summary>
public sealed class JsonTileStore : ITileStore
{
    private sealed record TileFile(Dictionary<string, List<UiTileDefinition>>? Pages);

    private readonly string _path;
    private readonly object _gate = new();

    public JsonTileStore(string path)
        => _path = path ?? throw new ArgumentNullException(nameof(path));

    /// <summary>The default store location (config folder next to the binaries).</summary>
    public static JsonTileStore Default()
        => new(Path.Combine(AppContext.BaseDirectory, "config", "dashboard.tiles.json"));

    public List<UiTileDefinition>? Load(string pageId)
    {
        lock (_gate)
        {
            if (!File.Exists(_path))
            {
                return null;
            }

            try
            {
                var file = JsonSerializer.Deserialize<TileFile>(File.ReadAllText(_path), UiLayoutLoader.Options);
                return file?.Pages is not null && file.Pages.TryGetValue(pageId, out var tiles) ? tiles : null;
            }
            catch (JsonException)
            {
                return null; // a corrupt edit file must not break the app; fall back to the defaults.
            }
        }
    }

    public void Save(string pageId, IReadOnlyList<UiTileDefinition> tiles)
    {
        lock (_gate)
        {
            var file = LoadFile() ?? new Dictionary<string, List<UiTileDefinition>>(StringComparer.Ordinal);
            file[pageId] = tiles.ToList();
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_path))!);
            File.WriteAllText(_path, JsonSerializer.Serialize(new TileFile(file), UiLayoutLoader.Options));
        }
    }

    public void Clear(string pageId)
    {
        lock (_gate)
        {
            var file = LoadFile();
            if (file is null || !file.Remove(pageId))
            {
                return;
            }

            if (file.Count == 0)
            {
                if (File.Exists(_path))
                {
                    File.Delete(_path);
                }

                return;
            }

            File.WriteAllText(_path, JsonSerializer.Serialize(new TileFile(file), UiLayoutLoader.Options));
        }
    }

    private Dictionary<string, List<UiTileDefinition>>? LoadFile()
    {
        if (!File.Exists(_path))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<TileFile>(File.ReadAllText(_path), UiLayoutLoader.Options)?.Pages;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
