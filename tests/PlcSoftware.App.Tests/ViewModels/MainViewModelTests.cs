using PlcSoftware.App.ViewModels;
using PlcSoftware.Core.Models;
using PlcSoftware.Core.Services;

namespace PlcSoftware.App.Tests.ViewModels;

/// <summary>
/// Pins how <see cref="MainViewModel"/> maps raw link/heartbeat/snapshot state into UI-ready
/// connection-heartbeat-mode-run-fault-mask state. These tests are deliberately free of any WPF
/// runtime dependency: the view model only consumes Core snapshots and state events, so the suite is
/// pure. They CANNOT run on the WSL/Linux cross-build (WindowsDesktop runtime is absent) — they are
/// CI-only on Windows. On Linux they only contribute a compile RED/GREEN check.
/// </summary>
public class MainViewModelTests
{
    private static IReadOnlyDictionary<string, object?> Snap(params (string Key, object? Value)[] values)
        => values.ToDictionary(v => v.Key, v => v.Value);

    private static IReadOnlyDictionary<int, string> DefaultFaults()
        => new Dictionary<int, string>
        {
            [1] = "急停",
            [2] = "安全门打开",
            [3] = "安全光栅",
        };

    [Fact]
    public void Connection_online_maps_to_online_text()
    {
        var vm = new MainViewModel();

        vm.ApplyConnectionState(ConnectionState.Online);

        Assert.Equal(ConnectionState.Online, vm.ConnectionState);
        Assert.Equal("在线", vm.ConnectionStatusText);
    }

    [Fact]
    public void Connection_reconnecting_maps_to_reconnect_text()
    {
        var vm = new MainViewModel();

        vm.ApplyConnectionState(ConnectionState.Reconnecting);

        Assert.Equal("重连中", vm.ConnectionStatusText);
    }

    [Fact]
    public void Connection_offline_displays_disconnected()
    {
        var vm = new MainViewModel();

        vm.ApplyConnectionState(ConnectionState.Disconnected);

        Assert.Equal("离线", vm.ConnectionStatusText);
    }

    [Fact]
    public void Heartbeat_online_maps_to_healthy_text()
    {
        var vm = new MainViewModel();

        vm.ApplyHeartbeat(HeartbeatStatus.Online);

        Assert.Equal(HeartbeatStatus.Online, vm.Heartbeat);
        Assert.Equal("心跳正常", vm.HeartbeatText);
    }

    [Fact]
    public void Heartbeat_lost_maps_to_lost_text()
    {
        var vm = new MainViewModel();

        vm.ApplyHeartbeat(HeartbeatStatus.Lost);

        Assert.Equal("心跳丢失", vm.HeartbeatText);
    }

    [Fact]
    public void Heartbeat_unknown_is_default()
    {
        var vm = new MainViewModel();

        Assert.Equal(HeartbeatStatus.Unknown, vm.Heartbeat);
        Assert.Equal("未知", vm.HeartbeatText);
    }

    [Fact]
    public void Snapshot_auto_bit_selects_auto_mode()
    {
        var vm = new MainViewModel();

        vm.ApplySnapshot(new DeviceSnapshot(Snap(("M2", true)), DateTime.UtcNow));

        Assert.Equal(MachineMode.Auto, vm.Mode);
        Assert.Equal("自动", vm.ModeText);
    }

    [Fact]
    public void Snapshot_bypass_bit_selects_bypass_mode()
    {
        var vm = new MainViewModel();

        vm.ApplySnapshot(new DeviceSnapshot(Snap(("M13", true)), DateTime.UtcNow));

        Assert.Equal(MachineMode.Bypass, vm.Mode);
        Assert.Equal("直通", vm.ModeText);
    }

    [Fact]
    public void Snapshot_manual_bit_selects_manual_mode()
    {
        var vm = new MainViewModel();

        vm.ApplySnapshot(new DeviceSnapshot(Snap(("M1", true)), DateTime.UtcNow));

        Assert.Equal(MachineMode.Manual, vm.Mode);
        Assert.Equal("手动", vm.ModeText);
    }

    [Fact]
    public void Snapshot_without_mode_bit_reports_unknown()
    {
        var vm = new MainViewModel();

        vm.ApplySnapshot(new DeviceSnapshot(Snap(), DateTime.UtcNow));

        Assert.Equal(MachineMode.Unknown, vm.Mode);
        Assert.Equal("未知", vm.ModeText);
    }

    [Fact]
    public void Snapshot_run_bit_maps_to_running()
    {
        var vm = new MainViewModel();

        vm.ApplySnapshot(new DeviceSnapshot(Snap(("M3", true)), DateTime.UtcNow));

        Assert.True(vm.IsRunning);
        Assert.Equal("运行", vm.RunText);
    }

    [Fact]
    public void Snapshot_without_run_bit_reports_stopped()
    {
        var vm = new MainViewModel();

        vm.ApplySnapshot(new DeviceSnapshot(Snap(("M3", false)), DateTime.UtcNow));

        Assert.False(vm.IsRunning);
        Assert.Equal("停止", vm.RunText);
    }

    [Fact]
    public void Snapshot_fault_code_resolves_message_and_fault_flag()
    {
        var vm = new MainViewModel(DefaultFaults());

        vm.ApplySnapshot(new DeviceSnapshot(Snap(("D110", (ushort)3)), DateTime.UtcNow));

        Assert.Equal(3, vm.FaultCode);
        Assert.True(vm.HasFault);
        Assert.Equal("安全光栅", vm.FaultText);
    }

    [Fact]
    public void Snapshot_unknown_fault_code_falls_back_to_code_text()
    {
        var vm = new MainViewModel(DefaultFaults());

        vm.ApplySnapshot(new DeviceSnapshot(Snap(("D110", (ushort)99)), DateTime.UtcNow));

        Assert.Equal(99, vm.FaultCode);
        Assert.True(vm.HasFault);
        Assert.Equal("故障码 99", vm.FaultText);
    }

    [Fact]
    public void Snapshot_zero_fault_code_clears_fault()
    {
        var vm = new MainViewModel(DefaultFaults());

        vm.ApplySnapshot(new DeviceSnapshot(Snap(("D110", (ushort)1)), DateTime.UtcNow));
        Assert.True(vm.HasFault);

        vm.ApplySnapshot(new DeviceSnapshot(Snap(("D110", (ushort)0)), DateTime.UtcNow));

        Assert.Equal(0, vm.FaultCode);
        Assert.False(vm.HasFault);
    }

    // Mask is sourced from the HMI's held command state (design §4.4), NOT from a snapshot: M110/M111 are
    // holding commands with no PLC feedback point, so the snapshot never carries them. The VM consumes the
    // held-state flags via ApplyMaskState (wired from SimpleHeldStateService).

    [Fact]
    public void ApplyMaskState_true_sets_bypass_flags_and_text()
    {
        var vm = new MainViewModel();

        vm.ApplyMaskState(true, true);

        Assert.True(vm.LightCurtainBypass);
        Assert.True(vm.DoorBypass);
        Assert.Equal("已屏蔽", vm.MaskText);
    }

    [Fact]
    public void ApplyMaskState_false_clears_bypass_flags_and_text()
    {
        var vm = new MainViewModel();

        vm.ApplyMaskState(false, false);

        Assert.False(vm.LightCurtainBypass);
        Assert.False(vm.DoorBypass);
        Assert.Equal("正常", vm.MaskText);
    }

    [Fact]
    public void ApplyMaskState_partial_flags_map_to_independent_state()
    {
        var vm = new MainViewModel();

        vm.ApplyMaskState(true, false);
        Assert.True(vm.LightCurtainBypass);
        Assert.False(vm.DoorBypass);
        Assert.Equal("已屏蔽", vm.MaskText);

        vm.ApplyMaskState(false, true);
        Assert.False(vm.LightCurtainBypass);
        Assert.True(vm.DoorBypass);
    }

    [Fact]
    public void Snapshot_does_not_read_mask_bits()
    {
        var vm = new MainViewModel();

        // Even a snapshot carrying M110/M111 must NOT flip the bypass flags: the snapshot is no longer the
        // source of the mask state (the point map has no slot for them), so those keys are ignored here.
        vm.ApplySnapshot(new DeviceSnapshot(
            Snap(("M110", true), ("M111", true)),
            DateTime.UtcNow));

        Assert.False(vm.LightCurtainBypass);
        Assert.False(vm.DoorBypass);
    }
}
