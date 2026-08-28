using PlcSoftware.Core.Configuration;
using PlcSoftware.Core.Models;

namespace PlcSoftware.Core.Tests.Configuration;

/// <summary>
/// Pins the ui-layout.json contract (design §7: 模块化可配置界面): the JSON shape, the enum-as-string
/// parsing, the default-page fallback and the validation rules (duplicate ids, dangling navigate targets,
/// unknown command targets, invalid parameter rows). The renderer is WPF-only (App layer), so everything
/// below runs on the Linux CI runner.
/// </summary>
public class UiLayoutLoaderTests
{
    private static string Minimal => """
        {
          "app": { "title": "自动化设备", "logo": "VISA" },
          "pages": [
            { "id": "login", "title": "登录",
              "modules": [ { "type": "header" }, { "type": "loginForm" } ] }
          ]
        }
        """;

    [Fact]
    public void Load_minimal_layout_parses_app_and_page()
    {
        var layout = UiLayoutLoader.Load(Minimal);

        Assert.Equal("自动化设备", layout.App.Title);
        Assert.Equal("VISA", layout.App.Logo);
        var page = Assert.Single(layout.Pages);
        Assert.Equal("login", page.Id);
        Assert.Equal("登录", page.Title);
        Assert.Equal(2, page.Modules.Count);
        Assert.Equal(UiModuleType.Header, page.Modules[0].Type);
        Assert.Equal(UiModuleType.LoginForm, page.Modules[1].Type);
    }

    [Fact]
    public void Default_page_falls_back_to_first_page_when_unset()
    {
        var layout = UiLayoutLoader.Load(Minimal);

        Assert.Equal("login", layout.DefaultPage.Id);
    }

    [Fact]
    public void Default_page_resolves_explicit_id()
    {
        var layout = UiLayoutLoader.Load("""
            {
              "app": { "defaultPage": "second" },
              "pages": [
                { "id": "first", "title": "一", "modules": [ { "type": "header" } ] },
                { "id": "second", "title": "二", "modules": [ { "type": "header" } ] }
              ]
            }
            """);

        Assert.Equal("second", layout.DefaultPage.Id);
    }

    [Fact]
    public void Full_layout_parses_all_module_types_and_actions()
    {
        var layout = UiLayoutLoader.Load("""
            {
              "app": {
                "title": "自动化设备", "logo": "VISA", "defaultPage": "login",
                "users": [ { "username": "admin", "password": "1234" } ],
                "loginSuccess": { "kind": "navigate", "page": "position-loading" }
              },
              "pages": [
                { "id": "login", "title": "登录", "modules": [
                  { "type": "header" },
                  { "type": "menu", "buttons": [
                      { "text": "手动模式", "action": { "kind": "command", "writes": [
                          { "target": "AutoMode", "value": false }, { "target": "BypassMode", "value": false } ] } },
                      { "text": "报警", "action": { "kind": "navigate", "page": "position-loading" } },
                      { "text": "登录", "action": { "kind": "login" } },
                      { "text": "上一页", "action": { "kind": "up" } },
                      { "text": "下一页", "action": { "kind": "down" } },
                      { "text": "返回", "action": { "kind": "back" } } ] },
                  { "type": "nav", "buttons": [ { "text": "型号选择", "action": { "kind": "none" } } ] },
                  { "type": "commandBar", "buttons": [
                      { "text": "启动", "action": { "kind": "command", "writes": [ { "target": "Start" } ] } },
                      { "text": "急停", "action": { "kind": "command", "writes": [ { "target": "EStopRequest" } ] } } ] },
                  { "type": "loginForm" }
                ] },
                { "id": "position-loading", "title": "位置参数-上料道", "modules": [
                  { "type": "header" },
                  { "type": "parameterGroup", "groups": [
                    { "title": "上料道一", "rows": [
                      { "axis": "X轴",
                        "position": { "register": "D126", "label": "位置设定(M)", "unit": "mm", "min": 0, "max": 1000 },
                        "speed": { "register": "D128", "label": "速度设定(M/S)", "unit": "M/S", "min": 1, "max": 20 } },
                      { "axis": "Y轴",
                        "position": { "register": "D130", "label": "位置设定(M)", "unit": "mm", "min": 0, "max": 1000 },
                        "speed": { "register": "D204", "label": "速度设定(M/S)", "unit": "M/S", "min": 1, "max": 20 } }
                    ] }
                  ] },
                  { "type": "pageHost", "hostedView": "OverviewView" }
                ] }
              ]
            }
            """);

        var login = layout.FindPage("login")!;
        var menu = login.Modules.Single(m => m.Type == UiModuleType.Menu);
        Assert.Equal(6, menu.Buttons.Count);
        Assert.Equal("手动模式", menu.Buttons[0].Text);
        var navigateAction = menu.Buttons[1].Action!;
        Assert.Equal(UiActionKind.Navigate, navigateAction.Kind);
        Assert.Equal("position-loading", navigateAction.Page);
        var commandAction = menu.Buttons[0].Action!;
        Assert.Equal(UiActionKind.Command, commandAction.Kind);
        Assert.Equal(2, commandAction.Writes.Count);
        Assert.Equal(CommandTarget.AutoMode, commandAction.Writes[0].ResolveTarget());
        Assert.False(commandAction.Writes[0].Value);
        Assert.Equal(UiActionKind.Login, menu.Buttons[2].Action!.Kind);
        Assert.Equal(UiActionKind.Up, menu.Buttons[3].Action!.Kind);
        Assert.Equal(UiActionKind.Down, menu.Buttons[4].Action!.Kind);
        Assert.Equal(UiActionKind.Back, menu.Buttons[5].Action!.Kind);

        var commandBar = login.Modules.Single(m => m.Type == UiModuleType.CommandBar);
        Assert.Equal(CommandTarget.Start, commandBar.Buttons[0].Action!.Writes[0].ResolveTarget());

        var position = layout.FindPage("position-loading")!;
        var group = position.Modules.Single(m => m.Type == UiModuleType.ParameterGroup).Groups.Single();
        Assert.Equal("上料道一", group.Title);
        Assert.Equal(2, group.Rows.Count);
        Assert.Equal("X轴", group.Rows[0].Axis);
        var xRow = group.Rows[0];
        Assert.Equal("D126", xRow.Position!.Register);
        Assert.Equal(1000, xRow.Position.Max);
        Assert.Equal(20, xRow.Speed!.Max);
        var hosted = position.Modules.Single(m => m.Type == UiModuleType.PageHost);
        Assert.Equal("OverviewView", hosted.HostedView);
        Assert.Equal(UiActionKind.Navigate, layout.App.LoginSuccess!.Kind);
    }

    [Fact]
    public void Empty_users_means_login_accepts_anything()
    {
        var layout = UiLayoutLoader.Load(Minimal);

        Assert.Empty(layout.App.Users);
    }

    [Fact]
    public void Login_required_defaults_to_true_when_users_are_configured()
    {
        var layout = UiLayoutLoader.Load("""
            {
              "app": { "users": [ { "username": "admin", "password": "1234" } ] },
              "pages": [ { "id": "a", "modules": [ { "type": "header" } ] } ]
            }
            """);

        Assert.True(layout.App.LoginRequired);
        Assert.Null(layout.App.RequireLogin);
    }

    [Fact]
    public void Login_required_defaults_to_false_without_users_and_can_be_overridden()
    {
        var layout = UiLayoutLoader.Load("""
            {
              "app": {},
              "pages": [ { "id": "a", "modules": [ { "type": "header" } ] } ]
            }
            """);
        Assert.False(layout.App.LoginRequired);

        var forced = UiLayoutLoader.Load("""
            {
              "app": { "requireLogin": true },
              "pages": [ { "id": "a", "modules": [ { "type": "header" } ] } ]
            }
            """);
        Assert.True(forced.App.LoginRequired);
    }

    [Fact]
    public void Dashboard_module_parses_all_tile_kinds()
    {
        var layout = UiLayoutLoader.Load("""
            {
              "app": { "defaultPage": "home" },
              "pages": [
                { "id": "home", "title": "首页", "modules": [
                  { "type": "dashboard", "tiles": [
                    { "id": "start", "kind": "button", "text": "启动", "action": { "kind": "command", "writes": [ { "target": "Start" } ] }, "cols": 3, "rows": 2, "color": "#1E6FB8" },
                    { "id": "mode", "kind": "status", "status": "Mode", "cols": 2, "rows": 2 },
                    { "id": "clock", "kind": "clock", "cols": 2, "rows": 1 },
                    { "id": "note", "kind": "text", "text": "生产看板", "cols": 2, "rows": 1 },
                    { "id": "alarm", "kind": "navigate", "text": "报警", "action": { "kind": "navigate", "page": "history" } }
                  ] }
                ] },
                { "id": "history", "title": "报警与历史", "modules": [ { "type": "header" } ] }
              ]
            }
            """);

        var home = layout.FindPage("home")!;
        var dashboard = home.Modules.Single(m => m.Type == UiModuleType.Dashboard);
        Assert.Equal(5, dashboard.Tiles.Count);

        var start = dashboard.Tiles[0];
        Assert.Equal(UiTileKind.Button, start.Kind);
        Assert.Equal("启动", start.Text);
        Assert.Equal(3, start.Cols);
        Assert.Equal(2, start.Rows);
        Assert.Equal(UiActionKind.Command, start.Action!.Kind);
        Assert.Equal("Start", start.Action.Writes[0].Target);

        Assert.Equal(UiTileKind.Status, dashboard.Tiles[1].Kind);
        Assert.Equal(UiTileStatus.Mode, dashboard.Tiles[1].Status);
        Assert.Equal(UiTileKind.Clock, dashboard.Tiles[2].Kind);
        Assert.Equal(UiTileKind.Text, dashboard.Tiles[3].Kind);
        Assert.Equal(UiTileKind.Navigate, dashboard.Tiles[4].Kind);
        Assert.Equal("history", dashboard.Tiles[4].Action!.Page);
    }

    [Fact]
    public void Button_tile_without_action_fails_validation()
    {
        var ex = Assert.Throws<UiLayoutValidationException>(() => UiLayoutLoader.Load("""
            {
              "pages": [
                { "id": "home", "modules": [
                  { "type": "dashboard", "tiles": [ { "id": "t1", "kind": "button", "text": "启动" } ] }
                ] }
              ]
            }
            """));

        Assert.Contains("has no action", ex.Message);
    }

    [Fact]
    public void Status_tile_without_status_fails_validation()
    {
        var ex = Assert.Throws<UiLayoutValidationException>(() => UiLayoutLoader.Load("""
            {
              "pages": [
                { "id": "home", "modules": [
                  { "type": "dashboard", "tiles": [ { "id": "t1", "kind": "status" } ] }
                ] }
              ]
            }
            """));

        Assert.Contains("without a status kind", ex.Message);
    }

    [Fact]
    public void Tile_size_out_of_range_fails_validation()
    {
        var ex = Assert.Throws<UiLayoutValidationException>(() => UiLayoutLoader.Load("""
            {
              "pages": [
                { "id": "home", "modules": [
                  { "type": "dashboard", "tiles": [ { "id": "t1", "kind": "clock", "cols": 6, "rows": 1 } ] }
                ] }
              ]
            }
            """));

        Assert.Contains("must be within 1..4", ex.Message);
    }

    [Fact]
    public void Duplicate_tile_ids_fail_validation()
    {
        var ex = Assert.Throws<UiLayoutValidationException>(() => UiLayoutLoader.Load("""
            {
              "pages": [
                { "id": "home", "modules": [
                  { "type": "dashboard", "tiles": [
                    { "id": "t1", "kind": "clock" }, { "id": "t1", "kind": "clock" } ] }
                ] }
              ]
            }
            """));

        Assert.Contains("duplicate tile id", ex.Message);
    }

    [Fact]
    public void Malformed_json_throws_validation_exception()
    {
        var ex = Assert.Throws<UiLayoutValidationException>(() => UiLayoutLoader.Load("{ not json"));

        Assert.Contains("not valid JSON", ex.Message);
    }

    [Fact]
    public void Duplicate_page_ids_fail_validation()
    {
        var ex = Assert.Throws<UiLayoutValidationException>(() => UiLayoutLoader.Load("""
            {
              "pages": [
                { "id": "a", "modules": [ { "type": "header" } ] },
                { "id": "a", "modules": [ { "type": "header" } ] }
              ]
            }
            """));

        Assert.Contains("duplicate page id", ex.Message);
    }

    [Fact]
    public void Dangling_navigate_target_fails_validation()
    {
        var ex = Assert.Throws<UiLayoutValidationException>(() => UiLayoutLoader.Load("""
            {
              "pages": [
                { "id": "a", "modules": [
                  { "type": "menu", "buttons": [ { "text": "x", "action": { "kind": "navigate", "page": "missing" } } ] }
                ] }
              ]
            }
            """));

        Assert.Contains("unknown page 'missing'", ex.Message);
    }

    [Fact]
    public void Unknown_command_target_fails_validation()
    {
        var ex = Assert.Throws<UiLayoutValidationException>(() => UiLayoutLoader.Load("""
            {
              "pages": [
                { "id": "a", "modules": [
                  { "type": "menu", "buttons": [ { "text": "x", "action": { "kind": "command", "writes": [ { "target": "LaunchRocket" } ] } } ] }
                ] }
              ]
            }
            """));

        Assert.Contains("unknown command target 'LaunchRocket'", ex.Message);
    }

    [Fact]
    public void Command_action_without_writes_fails_validation()
    {
        var ex = Assert.Throws<UiLayoutValidationException>(() => UiLayoutLoader.Load("""
            {
              "pages": [
                { "id": "a", "modules": [
                  { "type": "menu", "buttons": [ { "text": "x", "action": { "kind": "command" } } ] }
                ] }
              ]
            }
            """));

        Assert.Contains("no writes", ex.Message);
    }

    [Fact]
    public void Parameter_row_without_field_fails_validation()
    {
        var ex = Assert.Throws<UiLayoutValidationException>(() => UiLayoutLoader.Load("""
            {
              "pages": [
                { "id": "a", "modules": [
                  { "type": "parameterGroup", "groups": [
                    { "title": "g", "rows": [ { "axis": "X轴" } ] }
                  ] }
                ] }
              ]
            }
            """));

        Assert.Contains("declares no field", ex.Message);
    }

    [Fact]
    public void Empty_layout_fails_validation()
    {
        var ex = Assert.Throws<UiLayoutValidationException>(() => UiLayoutLoader.Load("{}"));

        Assert.Contains("at least one page", ex.Message);
    }

    [Fact]
    public void TryLoadFromFile_returns_null_when_file_missing()
    {
        Assert.Null(UiLayoutLoader.TryLoadFromFile("/nonexistent/ui-layout.json"));
    }

    [Fact]
    public void LoadFromFile_throws_when_file_missing()
    {
        Assert.Throws<UiLayoutValidationException>(() => UiLayoutLoader.LoadFromFile("/nonexistent/ui-layout.json"));
    }

    [Fact]
    public void Repository_sample_layout_is_valid()
    {
        // The shipped config/ui-layout.json must always pass validation (it is the default operator
        // screen); a breaking change to the model or the sample is a startup error for the app.
        var root = LocateRepositoryRoot();
        var path = Path.Combine(root, "config", "ui-layout.json");
        if (!File.Exists(path))
        {
            return; // source tree not present (e.g. CI packaging) — nothing to pin.
        }

        var layout = UiLayoutLoader.LoadFromFile(path);

        Assert.NotEmpty(layout.Pages);
        Assert.NotNull(layout.DefaultPage);
        Assert.Contains(layout.Pages, p => p.Id == layout.DefaultPage.Id);
    }

    private static string LocateRepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "PlcSoftware.sln")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? AppContext.BaseDirectory;
    }
}
