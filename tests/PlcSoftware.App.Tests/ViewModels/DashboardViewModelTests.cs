using PlcSoftware.App.Services;
using PlcSoftware.App.ViewModels;
using PlcSoftware.Core.Abstractions;
using PlcSoftware.Core.Configuration;
using PlcSoftware.Core.Models;
using PlcSoftware.Core.Services;

namespace PlcSoftware.App.Tests.ViewModels;

/// <summary>
/// Pins the dashboard board (设计 §7 磁贴看板): the tile set built from the dashboard module, the
/// saved-tile override, the edit operations (move up/down, resize clamp 1..4), the save/cancel/reset
/// persistence flow through <see cref="ITileStore"/>, the live status values mirrored from
/// <see cref="MainViewModel"/> and the clock tick. WPF-free (the renderer is exercised on the Windows
/// CI runner).
/// </summary>
public class DashboardViewModelTests
{
    private static string LayoutJson() => """
        {
          "app": { "title": "自动化设备", "logo": "VISA", "defaultPage": "home" },
          "pages": [
            { "id": "home", "title": "首页看板", "modules": [
              { "type": "dashboard", "tiles": [
                { "id": "start", "kind": "button", "text": "启动", "action": { "kind": "command", "writes": [ { "target": "Start" } ] }, "cols": 3, "rows": 2 },
                { "id": "run", "kind": "status", "text": "运行", "status": "Run", "cols": 2, "rows": 2 },
                { "id": "clock", "kind": "clock", "text": "当前时间", "cols": 2, "rows": 1 },
                { "id": "note", "kind": "text", "text": "看板", "cols": 2, "rows": 1 }
              ] }
            ] },
            { "id": "alarm", "title": "报警", "modules": [ { "type": "header" } ] }
          ]
        }
        """;

    private static (DashboardViewModel Vm, RecordingTileStore Store, RecordingNavigator Nav, RecordingCommandService Cmd) Build(
        MainViewModel? main = null, List<UiTileDefinition>? saved = null)
    {
        var layout = UiLayoutLoader.Load(LayoutJson());
        var page = layout.FindPage("home")!;
        var store = new RecordingTileStore(saved);
        var nav = new RecordingNavigator();
        var cmd = new RecordingCommandService();
        var vm = new DashboardViewModel(layout, page, store, nav, cmd, main ?? new MainViewModel());
        return (vm, store, nav, cmd);
    }

    [Fact]
    public void Board_builds_tiles_from_the_dashboard_module()
    {
        var (vm, _, _, _) = Build();

        Assert.Equal(4, vm.Tiles.Count);
        Assert.Equal("start", vm.Tiles[0].Id);
        Assert.Equal(UiTileKind.Button, vm.Tiles[0].Kind);
        Assert.Equal(3, vm.Tiles[0].Cols);
        Assert.Equal(2, vm.Tiles[0].Rows);
        Assert.Equal(UiTileKind.Status, vm.Tiles[1].Kind);
        Assert.Equal(UiTileStatus.Run, vm.Tiles[1].StatusKind);
        Assert.Equal(UiTileKind.Clock, vm.Tiles[2].Kind);
        Assert.Equal(UiTileKind.Text, vm.Tiles[3].Kind);
        Assert.True(vm.HasClock);
        Assert.Equal(2, vm.Pages.Count);
    }

    [Fact]
    public void Saved_tiles_override_the_layout_defaults()
    {
        var saved = new List<UiTileDefinition>
        {
            new() { Id = "custom", Kind = UiTileKind.Text, Text = "保存的磁贴", Cols = 4, Rows = 1 },
        };
        var (vm, _, _, _) = Build(saved: saved);

        Assert.Single(vm.Tiles);
        Assert.Equal("custom", vm.Tiles[0].Id);
        Assert.Equal("保存的磁贴", vm.Tiles[0].Text);
    }

    [Fact]
    public void Move_up_and_down_reorder_the_tiles()
    {
        var (vm, _, _, _) = Build();

        vm.MoveUp(vm.Tiles[1]);
        Assert.Equal("run", vm.Tiles[0].Id);

        vm.MoveDown(vm.Tiles[0]);
        Assert.Equal("start", vm.Tiles[0].Id);
    }

    [Fact]
    public void Resize_clamps_to_1_4_units()
    {
        var (vm, _, _, _) = Build();
        var tile = vm.Tiles[3]; // note: 2x2.

        vm.Resize(tile, +1, +1);
        Assert.Equal(3, tile.Cols); // 2 + 1.
        Assert.Equal(2, tile.Rows); // note is 1 row; 1 + 1.

        vm.Resize(tile, -10, -10);
        Assert.Equal(1, tile.Cols);
        Assert.Equal(1, tile.Rows);
    }

    [Fact]
    public void Save_persists_the_current_tiles_and_exits_edit_mode()
    {
        var (vm, store, _, _) = Build();
        vm.EditCommand.Execute(null);
        Assert.True(vm.IsEditing);
        vm.MoveUp(vm.Tiles[1]);           // run moves to the front.
        vm.Tiles[0].Owner.Resize(vm.Tiles[0], 4, 1); // run becomes 4 cols.

        vm.SaveTilesCommand.Execute(null);

        Assert.False(vm.IsEditing);
        var saved = Assert.Single(store.SavedPages);
        Assert.Equal("home", saved.Key);
        Assert.Equal(4, saved.Value.Count);
        Assert.Equal("run", saved.Value[0].Id);
        Assert.Equal(4, saved.Value[0].Cols); // 2 + 4 clamped to 4.
        Assert.Equal(3, saved.Value[0].Rows); // 2 + 1.
        Assert.Contains("已保存", vm.StatusText);
    }

    [Fact]
    public void Cancel_edit_restores_the_snapshot()
    {
        var (vm, _, _, _) = Build();
        vm.EditCommand.Execute(null);
        vm.MoveUp(vm.Tiles[1]);
        vm.Tiles[0].Owner.Resize(vm.Tiles[0], 4, 4);

        vm.CancelEditCommand.Execute(null);

        Assert.False(vm.IsEditing);
        Assert.Equal("start", vm.Tiles[0].Id);
        Assert.Equal(3, vm.Tiles[0].Cols);
    }

    [Fact]
    public void Reset_defaults_restores_the_module_tiles_and_clears_the_store()
    {
        var saved = new List<UiTileDefinition>
        {
            new() { Id = "custom", Kind = UiTileKind.Text, Text = "x", Cols = 2, Rows = 2 },
        };
        var (vm, store, _, _) = Build(saved: saved);
        Assert.Single(vm.Tiles);

        vm.ResetDefaultsCommand.Execute(null);

        Assert.Equal(4, vm.Tiles.Count);
        Assert.Equal("start", vm.Tiles[0].Id);
        Assert.Equal("home", store.ClearedPage);
    }

    [Fact]
    public void Status_tile_mirrors_the_main_view_model_state()
    {
        var main = new MainViewModel();
        var (vm, _, _, _) = Build(main: main);
        var runTile = vm.Tiles.Single(t => t.Id == "run");

        Assert.Equal("停止", runTile.Value); // default: not running.

        main.ApplySnapshot(TestSnapshot(isRunning: true));
        Assert.Equal("运行", runTile.Value);

        main.ApplySnapshot(TestSnapshot(isRunning: false));
        Assert.Equal("停止", runTile.Value);
    }

    [Fact]
    public void Clock_tick_updates_clock_tiles_only()
    {
        var (vm, _, _, _) = Build();
        var before = vm.Tiles.Single(t => t.Id == "clock").Value;

        vm.TickClock();

        var clock = vm.Tiles.Single(t => t.Id == "clock").Value!;
        Assert.NotEqual(before, clock);
        Assert.Matches(@"^\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}$", clock);
    }

    [Fact]
    public async Task Button_tile_executes_its_command_action()
    {
        var (vm, _, nav, cmd) = Build();

        await vm.Tiles[0].ClickCommand.ExecuteAsync(null);

        var request = Assert.Single(cmd.Requests);
        Assert.Equal(CommandTarget.Start, request.Target);
        Assert.True(request.Value);
        Assert.Contains("命令已执行", vm.StatusText);
    }

    [Fact]
    public async Task Tile_action_navigate_routes_to_the_navigator()
    {
        var (vm, _, nav, _) = Build();

        await vm.ExecuteActionAsync(new UiActionDefinition { Kind = UiActionKind.Navigate, Page = "alarm" }, CancellationToken.None);

        Assert.Equal("alarm", nav.LastNavigate);
    }

    // --- Fakes & helpers ---------------------------------------------------------------------------

    private static DeviceSnapshot TestSnapshot(bool isRunning)
        => new(new Dictionary<string, object?>
        {
            ["M3"] = isRunning, // M3 运行 bit.
            ["M1"] = true,      // M1 手动 mode (so ModeText resolves).
        }, DateTime.UtcNow);

    private sealed class RecordingTileStore : ITileStore
    {
        private readonly List<UiTileDefinition>? _saved;
        public Dictionary<string, List<UiTileDefinition>> SavedPages { get; } = new();
        public string? ClearedPage { get; private set; }

        public RecordingTileStore(List<UiTileDefinition>? saved) => _saved = saved;

        public List<UiTileDefinition>? Load(string pageId) => _saved;

        public void Save(string pageId, IReadOnlyList<UiTileDefinition> tiles) => SavedPages[pageId] = tiles.ToList();

        public void Clear(string pageId) => ClearedPage = pageId;
    }

    private sealed class RecordingNavigator : IConfigurableUiNavigator
    {
        public string? LastNavigate { get; private set; }

        public void Navigate(string pageId) => LastNavigate = pageId;
        public void NavigateDown() { }
        public void NavigateUp() { }
        public void NavigateBack() { }
        public void ShowLogin() { }
        public void SignIn(string username) { }
        public void SignOut() { }
    }

    private sealed class RecordingCommandService : ICommandService
    {
        public List<CommandRequest> Requests { get; } = new();

        public Task<CommandResult> ExecuteAsync(CommandRequest request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(new CommandResult(CommandStatus.Success, request.Target));
        }

        public Task ReleaseJogCommandsAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
