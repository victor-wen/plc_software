using PlcSoftware.App.ViewModels;
using PlcSoftware.Core.Abstractions;
using PlcSoftware.Core.Configuration;
using PlcSoftware.Core.Models;
using PlcSoftware.Core.Services;

namespace PlcSoftware.App.Tests.ViewModels;

/// <summary>
/// Pins the configurable-page view model (design §7 模块化可配置界面): the button groups built from the
/// configured modules, the sign-in flow (accept / reject / loginSuccess hook), the action execution
/// (navigate / up / down / back / command writes through <see cref="ICommandService"/>) and the
/// 位置参数 field write (range enforcement, write-then-verify via <see cref="ParameterService"/>).
///
/// <para><b>WPF-free.</b> No WPF type is touched here, so the tests only need the App assembly plus the
/// Core services; they still run on the Windows CI runner because the test project targets
/// net8.0-windows (same as every App test).</para>
/// </summary>
public class ConfigurablePageViewModelTests
{
    private static readonly CancellationToken None = CancellationToken.None;

    private static string LayoutJson(string appExtra = "", string pageExtra = "")
        => $$"""
            {
              "app": { "title": "自动化设备", "logo": "VISA", "defaultPage": "login" {{appExtra}} },
              "pages": [
                { "id": "login", "title": "登录", "modules": [
                  { "type": "header" },
                  { "type": "loginForm" },
                  { "type": "menu", "buttons": [
                      { "text": "自动模式", "action": { "kind": "command", "writes": [ { "target": "AutoMode" }, { "target": "BypassMode", "value": false } ] } },
                      { "text": "报警", "action": { "kind": "navigate", "page": "history" } },
                      { "text": "上一页", "action": { "kind": "up" } },
                      { "text": "下一页", "action": { "kind": "down" } },
                      { "text": "返回", "action": { "kind": "back" } },
                      { "text": "退出", "action": { "kind": "logout" } } ] },
                  { "type": "nav", "buttons": [ { "text": "辊道", "action": { "kind": "none" } } ] },
                  { "type": "commandBar", "buttons": [ { "text": "启动", "action": { "kind": "command", "writes": [ { "target": "Start" } ] } } ] }
                ] },
                { "id": "position", "title": "位置参数", "modules": [
                  { "type": "header" },
                  { "type": "parameterGroup", "groups": [
                    { "title": "上料道一", "rows": [
                      { "axis": "X轴",
                        "position": { "register": "D201", "label": "位置设定(M)", "unit": "mm", "min": 0, "max": 1000 },
                        "speed": { "register": "D202", "label": "速度设定(M/S)", "unit": "M/S", "min": 1, "max": 20 } } ] } ] }
                ] },
                { "id": "history", "title": "报警与历史", "modules": [ { "type": "header" } ] }
                {{pageExtra}}
              ]
            }
            """;

    private static (ConfigurablePageViewModel Vm, RecordingNavigator Nav, RecordingCommandService Cmd) Build(
        string pageId = "login", string? appExtra = null, string? pageExtra = null)
    {
        var layout = UiLayoutLoader.Load(LayoutJson(appExtra ?? "", pageExtra ?? ""));
        var page = layout.FindPage(pageId)!;
        var nav = new RecordingNavigator();
        var cmd = new RecordingCommandService();
        var vm = new ConfigurablePageViewModel(layout, page, nav, cmd, BuildParameterService());
        return (vm, nav, cmd);
    }

    [Fact]
    public void Page_exposes_header_and_button_groups_from_config()
    {
        var (vm, _, _) = Build();

        Assert.Equal("login", vm.PageId);
        Assert.Equal("登录", vm.Title);
        Assert.Equal("自动化设备", vm.HeaderTitle);
        Assert.Equal("VISA", vm.HeaderLogo);
        Assert.True(vm.HasHeader);
        Assert.True(vm.HasLoginForm);
        Assert.False(vm.HasParameterGroups);
        Assert.False(vm.HasHostedView);

        Assert.Equal(6, vm.MenuButtons.Count);
        Assert.Equal("自动模式", vm.MenuButtons[0].Text);
        Assert.Equal("报警", vm.MenuButtons[1].Text);
        Assert.Single(vm.NavButtons);
        Assert.Equal("辊道", vm.NavButtons[0].Text);
        Assert.Single(vm.CommandBarButtons);
        Assert.Equal("启动", vm.CommandBarButtons[0].Text);
    }

    [Fact]
    public async Task Command_button_sends_the_configured_writes_in_order()
    {
        var (vm, _, cmd) = Build();

        await vm.MenuButtons[0].ClickCommand.ExecuteAsync(null);

        Assert.Equal(2, cmd.Requests.Count);
        Assert.Equal(CommandTarget.AutoMode, cmd.Requests[0].Target);
        Assert.True(cmd.Requests[0].Value);
        Assert.Equal(CommandTarget.BypassMode, cmd.Requests[1].Target);
        Assert.False(cmd.Requests[1].Value);
        Assert.Contains("命令已执行", vm.StatusText);
    }

    [Fact]
    public async Task Navigate_action_routes_through_the_navigator()
    {
        var (vm, nav, _) = Build();

        await vm.MenuButtons[1].ClickCommand.ExecuteAsync(null);

        Assert.Equal("history", nav.LastNavigate);
    }

    [Fact]
    public async Task Up_down_back_actions_walk_the_page_list_and_history()
    {
        var (vm, nav, _) = Build();

        await vm.MenuButtons[2].ClickCommand.ExecuteAsync(null); // 上一页
        Assert.Equal("up", nav.LastKind);
        await vm.MenuButtons[3].ClickCommand.ExecuteAsync(null); // 下一页
        Assert.Equal("down", nav.LastKind);
        await vm.MenuButtons[4].ClickCommand.ExecuteAsync(null); // 返回
        Assert.Equal("back", nav.LastKind);
    }

    [Fact]
    public async Task Logout_action_clears_the_sign_in_state()
    {
        var (vm, nav, _) = Build();

        vm.Username = "admin";
        vm.Password = "1234";
        vm.LoginConfirmCommand.Execute(null); // no users configured → accepted (no loginSuccess hook here).
        Assert.True(vm.IsSignedIn);

        await vm.MenuButtons[5].ClickCommand.ExecuteAsync(null); // 退出

        Assert.False(vm.IsSignedIn);
        Assert.Equal("logout", nav.LastKind);
    }

    [Fact]
    public void Login_with_no_configured_users_accepts_anything()
    {
        var (vm, _, _) = Build();

        vm.Username = "anything";
        vm.Password = "whatever";
        vm.LoginConfirmCommand.Execute(null);

        Assert.True(vm.IsSignedIn);
        Assert.Null(vm.LoginError);
    }

    [Fact]
    public void Login_rejects_wrong_credentials_and_runs_success_hook_on_match()
    {
        var (vm, nav, _) = Build(appExtra: """ , "users": [ { "username": "admin", "password": "1234" } ], "loginSuccess": { "kind": "navigate", "page": "position" } """);

        vm.Username = "admin";
        vm.Password = "wrong";
        vm.LoginConfirmCommand.Execute(null);
        Assert.False(vm.IsSignedIn);
        Assert.Contains("用户名或密码错误", vm.LoginError);

        vm.Password = "1234";
        vm.LoginConfirmCommand.Execute(null);
        Assert.True(vm.IsSignedIn);
        Assert.Null(vm.LoginError);
        Assert.Equal("admin", nav.LastSignedInUser);
        Assert.Equal("position", nav.LastNavigate);
    }

    [Fact]
    public void Parameter_groups_expose_axis_rows_with_fields()
    {
        var (vm, _, _) = Build(pageId: "position");

        Assert.True(vm.HasParameterGroups);
        var group = Assert.Single(vm.ParameterGroups);
        Assert.Equal("上料道一", group.Title);
        var row = Assert.Single(group.Rows);
        Assert.Equal("X轴", row.Axis);
        Assert.NotNull(row.Position);
        Assert.Equal("D201", row.Position.Register);
        Assert.Equal("位置设定(M)", row.Position.Label);
        Assert.NotNull(row.Speed);
    }

    [Fact]
    public async Task Parameter_field_rejects_non_integer_input_without_writing()
    {
        var (vm, _, _) = Build(pageId: "position");
        var field = vm.ParameterGroups[0].Rows[0].Position!;

        field.InputText = "abc";
        await field.ConfirmCommand.ExecuteAsync(null);

        Assert.Contains("不是有效整数", field.StatusText);
    }

    [Fact]
    public async Task Parameter_field_rejects_out_of_range_input_without_writing()
    {
        var (vm, _, _) = Build(pageId: "position");
        var field = vm.ParameterGroups[0].Rows[0].Position!;

        field.InputText = "2000"; // max is 1000.
        await field.ConfirmCommand.ExecuteAsync(null);

        Assert.Contains("超出允许范围", field.StatusText);
    }

    [Fact]
    public async Task Parameter_field_writes_in_range_value_through_parameter_service()
    {
        var (vm, _, _) = Build(pageId: "position");
        var field = vm.ParameterGroups[0].Rows[0].Speed!;

        field.InputText = "5";
        await field.ConfirmCommand.ExecuteAsync(null);

        Assert.Contains("写入成功", field.StatusText);
    }

    // --- Fakes -------------------------------------------------------------------------------------

    /// <summary>Records the navigator calls instead of touching a window.</summary>
    private sealed class RecordingNavigator : IConfigurableUiNavigator
    {
        public string? LastNavigate { get; private set; }
        public string? LastKind { get; private set; }
        public string? LastSignedInUser { get; private set; }

        public void Navigate(string pageId) { LastNavigate = pageId; LastKind = "navigate"; }
        public void NavigateDown() => LastKind = "down";
        public void NavigateUp() => LastKind = "up";
        public void NavigateBack() => LastKind = "back";
        public void ShowLogin() => LastKind = "login";
        public void SignIn(string username) { LastSignedInUser = username; LastKind = "signin"; }
        public void SignOut() => LastKind = "logout";
    }

    /// <summary>Records command requests; every command succeeds.</summary>
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

    private static ParameterService BuildParameterService() => new(
        new InMemoryParameterClient(),
        new OnlineGate(),
        new[]
        {
            new ParameterDefinition { Name = "D201", Address = 101, Unit = "mm", Min = 0, Max = 1000 },
            new ParameterDefinition { Name = "D202", Address = 102, Unit = "M/S", Min = 1, Max = 20 },
        });

    /// <summary>Modbus client whose register file answers reads with the last written value
    /// (so ParameterService's write-then-verify reports Success).</summary>
    private sealed class InMemoryParameterClient : IModbusClient
    {
        private readonly Dictionary<ushort, ushort> _registers = new();

        public Task WriteSingleRegisterAsync(byte slaveId, ushort address, ushort value, CancellationToken cancellationToken)
        {
            _registers[address] = value;
            return Task.CompletedTask;
        }

        public Task<ushort[]> ReadHoldingRegistersAsync(byte slaveId, ushort address, ushort count, CancellationToken cancellationToken)
            => Task.FromResult(Enumerable.Range(0, count).Select(i => _registers.GetValueOrDefault((ushort)(address + i))).ToArray());

        public Task<bool[]> ReadDiscreteInputsAsync(byte slaveId, ushort address, ushort count, CancellationToken cancellationToken)
            => Task.FromResult(new bool[count]);

        public Task<bool[]> ReadCoilsAsync(byte slaveId, ushort address, ushort count, CancellationToken cancellationToken)
            => Task.FromResult(new bool[count]);

        public Task<ushort[]> ReadInputRegistersAsync(byte slaveId, ushort address, ushort count, CancellationToken cancellationToken)
            => Task.FromResult(new ushort[count]);

        public Task WriteSingleCoilAsync(byte slaveId, ushort address, bool value, CancellationToken cancellationToken)
            => Task.CompletedTask;

        public Task ConnectAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task DisconnectAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>Online + manual-idle gate.</summary>
    private sealed class OnlineGate : ICommandGate
    {
        public bool IsOnline => true;

        public bool IsManualIdle => true;
    }
}
