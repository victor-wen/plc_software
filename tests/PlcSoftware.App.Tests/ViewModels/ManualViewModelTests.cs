using PlcSoftware.App.ViewModels;
using PlcSoftware.Core.Abstractions;
using PlcSoftware.Core.Models;

namespace PlcSoftware.App.Tests.ViewModels;

/// <summary>
/// Pins how <see cref="ManualViewModel"/> gates and releases the manual press-and-hold jogs (design §6.4):
/// 调宽正转 (M106) / 调宽反转 (M107) / 皮带点动 (M108) / 挡停 (M109).
///
/// <para><b>CanExecute (design §6.4).</b> A jog only opens when the machine is manual-idle
/// (<see cref="ICommandGate.IsManualIdle"/>: M1 manual + !M3 stopped) — the same rule the PLC enforces as
/// the final interlock. Every other link/mode/run state denies the jog (design §5.2/§6.4: 断线 and 非手动运行
/// 状态 both deny manual output).</para>
///
/// <para><b>Release (design §6.4).</b> Mouse release / mouse-leave / focus loss / page switch / window close
/// all converge on <see cref="ManualViewModel.ReleaseAllJogsAsync"/>, which routes to
/// <see cref="ICommandService.ReleaseJogCommandsAsync"/> and writes M106-M109 all false — no manual coil can
/// be left latched when the operator releases or navigates away. The four triggers each call the SAME VM
/// method (see the direct-call tests below); the actual <em>event-raising</em> trigger wiring (a hosted
/// <c>Button</c> + <c>PressAndHoldBehavior</c> raising mouse/focus events, and the <c>MainWindow</c>
/// closing / page-switch hooks) is pinned against the same method in the Windows-CI-only
/// <c>ManualReleaseTriggerTests</c>.</para>
///
/// <para><b>No WPF dependency.</b> The view model consumes Core snapshots + <see cref="ConnectionState"/>
/// through <see cref="ManualViewModel.ApplyConnectionState"/> / <see cref="ManualViewModel.ApplySnapshot"/>
/// and executes writes through the injected <see cref="ICommandService"/>, gated by the injected
/// <see cref="ICommandGate"/>. The suite is WPF-runtime-free: it CANNOT run on the WSL/Linux cross-build
/// (WindowsDesktop runtime absent) — on Linux it only contributes a compile RED/GREEN check; full
/// execution (GREEN) happens on the Windows CI runner.</para>
/// </summary>
public class ManualViewModelTests
{
    /// <summary>Builds an online manual-idle VM whose fake gate is open and fake service records requests.</summary>
    private static (FakeCommandGate Gate, FakeCommandService Service, ManualViewModel Vm) ManualIdle()
    {
        var gate = new FakeCommandGate { IsOnline = true, IsManualIdle = true };
        var service = new FakeCommandService();
        var vm = new ManualViewModel(service, gate);
        vm.ApplyConnectionState(ConnectionState.Online);
        return (gate, service, vm);
    }

    // --- Jogs open ONLY in the manual-idle state (design §6.4: 只在 PLC 返回手动且非运行状态时开放) ---------

    [Fact]
    public void Jog_commands_open_only_when_manual_idle()
    {
        // Online + manual + stopped (M1 && !M3): the jogs open.
        var (_, _, vm) = ManualIdle();
        Assert.True(vm.IsOnline);
        Assert.True(vm.IsManualIdle);
        Assert.True(vm.WidthPlusJogCommand.CanExecute(null));
        Assert.True(vm.WidthMinusJogCommand.CanExecute(null));
        Assert.True(vm.BeltJogCommand.CanExecute(null));
        Assert.True(vm.StopperJogCommand.CanExecute(null));

        // Offline: §5.3 disables every write.
        var offlineGate = new FakeCommandGate { IsOnline = false, IsManualIdle = false };
        var offlineVm = new ManualViewModel(new FakeCommandService(), offlineGate);
        offlineVm.ApplyConnectionState(ConnectionState.Disconnected);
        Assert.False(offlineVm.WidthPlusJogCommand.CanExecute(null));
        Assert.False(offlineVm.WidthMinusJogCommand.CanExecute(null));
        Assert.False(offlineVm.BeltJogCommand.CanExecute(null));
        Assert.False(offlineVm.StopperJogCommand.CanExecute(null));

        // Online but not manual-idle (auto / running): 非手动运行状态 denies every jog.
        var notIdleGate = new FakeCommandGate { IsOnline = true, IsManualIdle = false };
        var notIdleVm = new ManualViewModel(new FakeCommandService(), notIdleGate);
        notIdleVm.ApplyConnectionState(ConnectionState.Online);
        Assert.False(notIdleVm.WidthPlusJogCommand.CanExecute(null));
        Assert.False(notIdleVm.WidthMinusJogCommand.CanExecute(null));
        Assert.False(notIdleVm.BeltJogCommand.CanExecute(null));
        Assert.False(notIdleVm.StopperJogCommand.CanExecute(null));
    }

    /// <summary>The presentation flag mirrors <see cref="ICommandGate.IsManualIdle"/> so the buttons disable
    /// before the behavior can fire a press (design §6.4).</summary>
    [Fact]
    public void Jog_enabled_flag_follows_manual_idle()
    {
        var (gate, _, vm) = ManualIdle();
        Assert.True(vm.IsJogEnabled);
        Assert.Equal("可点动", vm.JogAvailabilityText);

        gate.IsManualIdle = false;
        vm.ApplySnapshot(new DeviceSnapshot(new Dictionary<string, object?>(), DateTime.UtcNow));

        Assert.False(vm.IsJogEnabled);
        Assert.Equal("不可点动", vm.JogAvailabilityText);
    }

    // --- Press (writes the targeted jog coil true) ----------------------------------------------------

    [Fact]
    public async Task Jog_press_writes_the_targeted_coil_true()
    {
        var (_, service, vm) = ManualIdle();

        // The press command (invoked by PressAndHoldBehavior.MouseLeftButtonDown) writes the jog coil true.
        await vm.WidthPlusJogCommand.ExecuteAsync(null);

        var request = Assert.Single(service.ExecuteRequests);
        Assert.Equal(CommandTarget.ManualWidthPlus, request.Target);
        Assert.True(request.Value);
    }

    [Fact]
    public void Jog_press_that_is_not_manual_idle_is_ignored()
    {
        var gate = new FakeCommandGate { IsOnline = true, IsManualIdle = false };
        var service = new FakeCommandService();
        var vm = new ManualViewModel(service, gate);
        vm.ApplyConnectionState(ConnectionState.Online);

        vm.PressJog(CommandTarget.ManualBeltJog);

        Assert.Empty(service.ExecuteRequests);
    }

    // A jog press targeting anything OTHER than the four M106-M109 jog coils is a wiring/config bug (a jog
    // button pointed at a mode/EStop/mask coil) — it must fail fast, not be silently ignored.

    [Fact]
    public void Jog_press_non_jog_target_throws()
    {
        var (_, service, vm) = ManualIdle();

        var ex = Assert.Throws<ArgumentException>(() => vm.PressJog(CommandTarget.AutoMode));

        Assert.Equal("target", ex.ParamName);
        Assert.Empty(service.ExecuteRequests);
    }

    // --- Release (design §6.4: 松开鼠标 / 切页 / 窗口失焦 / 应用退出 均复位命令) ------------------------------
    // VM-level direct-call tests: every trigger converges on the SAME ReleaseAllJogsAsync. The real
    // event-raising trigger tests (hosted Button + PressAndHoldBehavior, and the MainWindow closing &
    // page-switch hooks) live in the Windows-CI-only ManualReleaseTriggerTests.

    [Fact]
    public async Task Mouse_release_trigger_vm_route_releases_all_coils()
    {
        var (_, service, vm) = ManualIdle();
        vm.PressJog(CommandTarget.ManualStopper);

        await vm.ReleaseAllJogsAsync();

        Assert.Equal(1, service.ReleaseJogCommandCalls);
    }

    [Fact]
    public async Task Focus_loss_trigger_vm_route_releases_all_coils()
    {
        var (_, service, vm) = ManualIdle();

        await vm.ReleaseAllJogsAsync();

        Assert.Equal(1, service.ReleaseJogCommandCalls);
    }

    [Fact]
    public async Task Page_switch_trigger_vm_route_releases_all_coils()
    {
        var (_, service, vm) = ManualIdle();

        await vm.ReleaseAllJogsAsync();

        Assert.Equal(1, service.ReleaseJogCommandCalls);
    }

    [Fact]
    public async Task Window_close_trigger_vm_route_releases_all_coils()
    {
        var (_, service, vm) = ManualIdle();

        await vm.ReleaseAllJogsAsync();

        Assert.Equal(1, service.ReleaseJogCommandCalls);
    }

    /// <summary>A release must be safe even when offline (ReleaseJogCommandsAsync is a best-effort no-op): it
    /// must not throw out of a fire-and-forget event handler.</summary>
    [Fact]
    public async Task Release_all_jogs_never_throws()
    {
        var gate = new FakeCommandGate { IsOnline = false, IsManualIdle = false };
        var vm = new ManualViewModel(new FakeCommandService(), gate);

        await vm.ReleaseAllJogsAsync(); // must not throw.

        Assert.False(vm.IsOnline);
    }

    /// <summary>Read-only <see cref="ICommandGate"/> the tests control directly.</summary>
    private sealed class FakeCommandGate : ICommandGate
    {
        public bool IsOnline { get; set; }
        public bool IsManualIdle { get; set; }
    }

    /// <summary>Records executed <see cref="CommandRequest"/>s and <see cref="ICommandService.ReleaseJogCommandsAsync"/>
    /// calls so the release paths are observable at the VM level.</summary>
    private sealed class FakeCommandService : ICommandService
    {
        public List<CommandRequest> ExecuteRequests { get; } = new();
        public int ReleaseJogCommandCalls { get; private set; }

        public Task<CommandResult> ExecuteAsync(CommandRequest request, CancellationToken cancellationToken)
        {
            ExecuteRequests.Add(request);
            return Task.FromResult(new CommandResult(CommandStatus.Success, request.Target));
        }

        public Task ReleaseJogCommandsAsync(CancellationToken cancellationToken)
        {
            ReleaseJogCommandCalls++;
            return Task.CompletedTask;
        }
    }
}
