using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PlcSoftware.App.Services;
using PlcSoftware.Core.Abstractions;
using PlcSoftware.Core.Configuration;
using PlcSoftware.Core.Models;
using PlcSoftware.Core.Services;

namespace PlcSoftware.App.ViewModels;

/// <summary>
/// The home dashboard board (设计 §7 磁贴看板): a tile grid built from the dashboard module of the
/// page, whose tiles are either live shell-state cells (<see cref="UiTileStatus"/>), clock, text
/// annotations, command buttons or page shortcuts. The operator can enter an edit mode that resizes
/// tiles (1..4 grid columns/rows), reorders them and reconfigures their content; the edits are
/// persisted through the injected <see cref="ITileStore"/> (separate from ui-layout.json) and can be
/// reset to the layout defaults.
///
/// <para><b>Live state.</b> The optional <see cref="MainViewModel"/> is the status source: the board
/// mirrors its shell-state text properties (connection/heartbeat/mode/run/fault/mask), so a status tile
/// always shows what the status bar shows. Without a source the tiles keep "—".</para>
///
/// <para><b>WPF-free.</b> All state and commands are plain observable objects; the clock tick is driven
/// by the renderer calling <see cref="TickClock"/>, and the board view only maps commands onto WPF.</para>
/// </summary>
public sealed partial class DashboardViewModel : ObservableObject
{
    /// <summary>The tile grid width in columns (the unit width is total/columns).</summary>
    public const int GridColumns = 12;

    /// <summary>The minimum/maximum tile size in grid units.</summary>
    public const int MinUnits = 1;
    public const int MaxUnits = 4;

    private readonly string _pageId;
    private readonly ITileStore _store;
    private readonly List<UiTileDefinition> _defaults;
    private readonly IConfigurableUiNavigator _navigator;
    private readonly ICommandService _commandService;
    private readonly MainViewModel? _main;
    private List<UiTileDefinition> _editSnapshot = new();

    [ObservableProperty]
    private bool _isEditing;

    [ObservableProperty]
    private string _statusText = string.Empty;

    public DashboardViewModel(
        UiLayoutDefinition layout,
        UiPageDefinition page,
        ITileStore store,
        IConfigurableUiNavigator navigator,
        ICommandService commandService,
        MainViewModel? main = null)
    {
        _pageId = page?.Id ?? throw new ArgumentNullException(nameof(page));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _navigator = navigator ?? throw new ArgumentNullException(nameof(navigator));
        _commandService = commandService ?? throw new ArgumentNullException(nameof(commandService));
        _main = main;
        _defaults = page.Modules.FirstOrDefault(m => m.Type == UiModuleType.Dashboard)?.Tiles
            .Select(CloneTile).ToList() ?? new List<UiTileDefinition>();
        _editSnapshot = CloneList(_defaults);
        Pages = layout?.Pages.Select(p => new PageOption(p.Id, string.IsNullOrWhiteSpace(p.Title) ? p.Id : p.Title)).ToList()
            ?? new List<PageOption>();

        var saved = _store.Load(_pageId);
        foreach (var tile in saved ?? _defaults)
        {
            Tiles.Add(new TileViewModel(this, tile));
        }

        if (_main is not null)
        {
            _main.PropertyChanged += OnMainStateChanged;
        }

        RefreshStatusTiles();
    }

    /// <summary>The page list (navigate-tile editor selection).</summary>
    public IReadOnlyList<PageOption> Pages { get; }

    /// <summary>The tiles of the board (ordered top-left → bottom-right).</summary>
    public ObservableCollection<TileViewModel> Tiles { get; } = new();

    /// <summary>True when the board has no tiles (nothing to render).</summary>
    public bool HasTiles => Tiles.Count > 0;

    /// <summary>True when any tile is a clock (the renderer then runs the second timer).</summary>
    public bool HasClock => Tiles.Any(t => t.Kind == UiTileKind.Clock);

    /// <summary>Enters the edit mode (a snapshot is kept for cancel).</summary>
    [RelayCommand]
    private void Edit()
    {
        _editSnapshot = CloneList(Tiles.Select(t => t.ToDefinition()));
        IsEditing = true;
    }

    /// <summary>Shrinks/restores the board from the edit snapshot.</summary>
    [RelayCommand]
    private void CancelEdit()
    {
        ReplaceTiles(_editSnapshot);
        IsEditing = false;
    }

    /// <summary>Persists the current tiles to the store.</summary>
    [RelayCommand]
    private void SaveTiles()
    {
        // Drop the default-id collision guard: ids must stay unique in the saved set as well.
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var definitions = new List<UiTileDefinition>();
        foreach (var tile in Tiles)
        {
            var definition = tile.ToDefinition();
            if (!string.IsNullOrWhiteSpace(definition.Id) && seen.Add(definition.Id))
            {
                definitions.Add(definition);
            }
        }

        _store.Save(_pageId, definitions);
        IsEditing = false;
        StatusText = "磁贴布局已保存。";
    }

    /// <summary>Restores the layout-default tiles (the saved edits are removed).</summary>
    [RelayCommand]
    private void ResetDefaults()
    {
        _store.Clear(_pageId);
        ReplaceTiles(_defaults);
        StatusText = "已恢复默认布局。";
    }

    /// <summary>Swaps <paramref name="tile"/> one position up (toward the start).</summary>
    public void MoveUp(TileViewModel tile)
    {
        var index = Tiles.IndexOf(tile);
        if (index > 0)
        {
            Tiles.Move(index, index - 1);
        }
    }

    /// <summary>Swaps <paramref name="tile"/> one position down (toward the end).</summary>
    public void MoveDown(TileViewModel tile)
    {
        var index = Tiles.IndexOf(tile);
        if (index >= 0 && index < Tiles.Count - 1)
        {
            Tiles.Move(index, index + 1);
        }
    }

    /// <summary>Resizes a tile by the given column/row delta (clamped to 1..4).</summary>
    internal void Resize(TileViewModel tile, int colDelta, int rowDelta)
    {
        tile.Cols = Math.Clamp(tile.Cols + colDelta, MinUnits, MaxUnits);
        tile.Rows = Math.Clamp(tile.Rows + rowDelta, MinUnits, MaxUnits);
    }

    /// <summary>Executes a tile action (command writes or navigation); failures surface on
    /// <see cref="StatusText"/> and never throw.</summary>
    public async Task ExecuteActionAsync(UiActionDefinition action, CancellationToken cancellationToken)
    {
        if (action is null)
        {
            return;
        }

        switch (action.Kind)
        {
            case UiActionKind.Navigate:
                _navigator.Navigate(action.Page!);
                break;
            case UiActionKind.Command:
                await SendCommandAsync(action, cancellationToken);
                break;
            default:
                break;
        }
    }

    /// <summary>Updates clock tiles (called once per second by the renderer).</summary>
    public void TickClock()
    {
        var now = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        foreach (var tile in Tiles.Where(t => t.Kind == UiTileKind.Clock))
        {
            tile.Value = now;
        }
    }

    private async Task SendCommandAsync(UiActionDefinition action, CancellationToken cancellationToken)
    {
        StatusText = string.Empty;
        try
        {
            foreach (var write in action.Writes)
            {
                var target = write.ResolveTarget();
                if (target is null)
                {
                    StatusText = $"未知命令目标：{write.Target}";
                    return;
                }

                var result = await _commandService.ExecuteAsync(new CommandRequest(target.Value, write.Value), cancellationToken);
                if (result.Status != CommandStatus.Success)
                {
                    StatusText = $"命令失败：{result.Message ?? result.Status.ToString()}";
                    return;
                }
            }

            StatusText = "命令已执行";
        }
        catch (OperationCanceledException)
        {
            StatusText = "命令已取消";
        }
        catch (Exception ex)
        {
            StatusText = $"命令失败：{ex.Message}";
        }
    }

    private void OnMainStateChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is null or nameof(_main.ConnectionStatusText)
            or nameof(_main.HeartbeatText) or nameof(_main.ModeText) or nameof(_main.RunText)
            or nameof(_main.FaultText) or nameof(_main.MaskText) or nameof(_main.HasFault)
            or nameof(_main.ConnectionState) or nameof(_main.Heartbeat) or nameof(_main.Mode)
            or nameof(_main.IsRunning) or nameof(_main.LightCurtainBypass) or nameof(_main.DoorBypass))
        {
            RefreshStatusTiles();
        }
    }

    /// <summary>Re-reads every status tile from the main view model.</summary>
    private void RefreshStatusTiles()
    {
        if (_main is null)
        {
            return;
        }

        foreach (var tile in Tiles)
        {
            if (tile.Kind == UiTileKind.Status)
            {
                tile.Value = tile.StatusKind switch
                {
                    UiTileStatus.Connection => _main.ConnectionStatusText,
                    UiTileStatus.Heartbeat => _main.HeartbeatText,
                    UiTileStatus.Mode => _main.ModeText,
                    UiTileStatus.Run => _main.RunText,
                    UiTileStatus.Fault => _main.FaultText ?? "无故障",
                    UiTileStatus.Mask => _main.MaskText,
                    _ => "—",
                };
            }
        }
    }

    private void ReplaceTiles(IEnumerable<UiTileDefinition> definitions)
    {
        Tiles.Clear();
        foreach (var definition in definitions)
        {
            Tiles.Add(new TileViewModel(this, definition));
        }

        RefreshStatusTiles();
    }

    private static List<UiTileDefinition> CloneList(IEnumerable<UiTileDefinition> tiles)
        => tiles.Select(CloneTile).ToList();

    private static UiTileDefinition CloneTile(UiTileDefinition tile) => new()
    {
        Id = tile.Id,
        Kind = tile.Kind,
        Text = tile.Text,
        Action = tile.Action is null ? null : CloneAction(tile.Action),
        Status = tile.Status,
        Cols = tile.Cols,
        Rows = tile.Rows,
        Color = tile.Color,
    };

    private static UiActionDefinition CloneAction(UiActionDefinition action) => new()
    {
        Kind = action.Kind,
        Page = action.Page,
        Writes = action.Writes.Select(w => new UiWriteDefinition { Target = w.Target, Value = w.Value }).ToList(),
    };
}

/// <summary>A dashboard page selection entry (page id + title) for the navigate-tile editor.</summary>
public sealed class PageOption
{
    public PageOption(string id, string title)
    {
        Id = id;
        Title = title;
    }

    public string Id { get; }

    public string Title { get; }
}

/// <summary>
/// One dashboard tile: its config (kind/text/action/status/size) plus the live value for status/clock
/// tiles and the edit-mode controls (target picker state). The owner board applies the edit operations.
/// </summary>
public sealed partial class TileViewModel : ObservableObject
{
    private readonly DashboardViewModel _owner;

    [ObservableProperty]
    private string _text;

    [ObservableProperty]
    private int _cols;

    [ObservableProperty]
    private int _rows;

    [ObservableProperty]
    private string? _value;

    [ObservableProperty]
    private bool _isBusy;

    /// <summary>The currently configured command target name (edit mode), or null.</summary>
    [ObservableProperty]
    private string? _target;

    /// <summary>The currently configured command target value (edit mode, holding writes).</summary>
    [ObservableProperty]
    private bool _targetValue = true;

    /// <summary>The currently configured navigate target page id (edit mode), or null.</summary>
    [ObservableProperty]
    private string? _page;

    /// <summary>The currently chosen status kind (edit mode), or null.</summary>
    [ObservableProperty]
    private UiTileStatus? _statusKind;

    public TileViewModel(DashboardViewModel owner, UiTileDefinition definition)
    {
        _owner = owner ?? throw new ArgumentNullException(nameof(owner));
        Id = definition.Id;
        Kind = definition.Kind;
        _text = definition.Text;
        _cols = Math.Clamp(definition.Cols, DashboardViewModel.MinUnits, DashboardViewModel.MaxUnits);
        _rows = Math.Clamp(definition.Rows, DashboardViewModel.MinUnits, DashboardViewModel.MaxUnits);
        Color = definition.Color;
        _statusKind = definition.Status;
        _target = definition.Action?.Writes.FirstOrDefault()?.Target;
        _targetValue = definition.Action?.Writes.FirstOrDefault()?.Value ?? true;
        _page = definition.Action?.Page;
        _action = definition.Action is null ? null : CloneAction(definition.Action);
    }

    public string Id { get; }

    public UiTileKind Kind { get; }

    /// <summary>The tile color (ARGB hex from config), or null for the default.</summary>
    public string? Color { get; }

    /// <summary>The board owning this tile (edit operations).</summary>
    public DashboardViewModel Owner => _owner;

    private UiActionDefinition? _action;

    /// <summary>The tile's current action (kept in sync by the edit mode confirm).</summary>
    public UiActionDefinition? Action => _action;

    /// <summary>Runs the tile action (command writes / navigation).</summary>
    [RelayCommand]
    private async Task ClickAsync(CancellationToken cancellationToken)
    {
        if (_action is not null)
        {
            IsBusy = true;
            try
            {
                await _owner.ExecuteActionAsync(_action, cancellationToken);
            }
            finally
            {
                IsBusy = false;
            }
        }
    }

    /// <summary>Applies the edit-mode fields (text / target / target value / page / status) to the action.</summary>
    public void CommitEdit()
    {
        if (Kind is UiTileKind.Button or UiTileKind.Navigate)
        {
            if (Kind == UiTileKind.Button)
            {
                UiActionDefinition? action = null;
                if (!string.IsNullOrWhiteSpace(Target) && Enum.TryParse<CommandTarget>(Target, out _))
                {
                    action = new UiActionDefinition
                    {
                        Kind = UiActionKind.Command,
                        Writes = { new UiWriteDefinition { Target = Target, Value = TargetValue } },
                    };
                }

                _action = action;
            }
            else
            {
                _action = string.IsNullOrWhiteSpace(Page)
                    ? null
                    : new UiActionDefinition { Kind = UiActionKind.Navigate, Page = Page };
            }
        }
        else if (Kind == UiTileKind.Status)
        {
            // StatusKind is already set by the editor ComboBox; nothing further to sync.
        }
    }

    /// <summary>Converts the tile back to the config model (after <see cref="CommitEdit"/>).</summary>
    public UiTileDefinition ToDefinition() => new()
    {
        Id = Id,
        Kind = Kind,
        Text = Text,
        Action = _action is null ? null : CloneAction(_action),
        Status = Kind == UiTileKind.Status ? StatusKind : null,
        Cols = Cols,
        Rows = Rows,
        Color = Color,
    };

    private static UiActionDefinition CloneAction(UiActionDefinition action) => new()
    {
        Kind = action.Kind,
        Page = action.Page,
        Writes = action.Writes.Select(w => new UiWriteDefinition { Target = w.Target, Value = w.Value }).ToList(),
    };
}
