using PlcSoftware.App.ViewModels;
using PlcSoftware.Core.Abstractions;
using PlcSoftware.Core.Models;

namespace PlcSoftware.App.Tests.ViewModels;

/// <summary>
/// Pins how <see cref="OperationViewModel"/> gates and composes the operation-zone commands (design §6.3):
/// 启动 (M101) / 停止 (M102) / 复位 (M103) / 急停请求 (M100) and the 自动 (M104) / 手动 (M104=0,M105=0) /
/// 直通 (M105) mode switches. Every command is disabled while offline (design §5.3 forbids all writes);
/// the per-command rules additionally use the PLC mode, run and fault state as a best-effort pre-gate
/// (the PLC performs the final interlock, design §6.3).
///
/// <para><b>Mode confirmation via PLC read-back (design §4.4).</b> The UI never trusts a single write
/// result: after the mutually-exclusive M104/M105 writes succeed the VM only records a
/// <see cref="OperationViewModel.PendingMode"/>; the displayed <see cref="OperationViewModel.Mode"/> and
/// the "switched" indicator flip only once a snapshot reports M1/M2/M13.</para>
///
/// <para><b>No WPF dependency.</b> The view model consumes Core snapshots + <see cref="ConnectionState"/>
/// through <see cref="OperationViewModel.ApplySnapshot"/> / <see cref="OperationViewModel.ApplyConnectionState"/>
/// and executes writes through the injected <see cref="ICommandService"/>, gated by the injected
/// <see cref="ICommandGate"/>. The suite is WPF-runtime-free: it CANNOT run on the WSL/Linux cross-build
/// (WindowsDesktop runtime absent) — on Linux it only contributes a compile RED/GREEN check; full
/// execution (GREEN) happens on the Windows CI runner.</para>
/// </summary>
public class OperationViewModelTests
{
    private static IReadOnlyDictionary<string, object?> Snap(params (string Key, object? Value)[] values)
        => values.ToDictionary(v => v.Key, v => v.Value);

    /// <summary>Standard online machine snapshot: manual (M1), stopped (!M3), no fault (D110=0).</summary>
    private static DeviceSnapshot ManualStopped()
        => new DeviceSnapshot(Snap(("M1", true), ("M3", false), ("D110", (ushort)0)), DateTime.UtcNow);

    /// <summary>Builds an online VM whose fake gate is Online and fake service records requests.</summary>
    private static (FakeCommandGate Gate, FakeCommandService Service, OperationViewModel Vm) Online()
    {
        var gate = new FakeCommandGate { IsOnline = true, IsManualIdle = true };
        var service = new FakeCommandService();
        var vm = new OperationViewModel(service, gate);
        vm.ApplyConnectionState(ConnectionState.Online);
        return (gate, service, vm);
    }

    // --- Per-command CanExecute rules (design §6.3: 连接状态 / PLC 模式 / 运行状态 / 故障状态) -----------

    [Fact]
    public void Offline_disables_all_write_commands()
    {
        var gate = new FakeCommandGate { IsOnline = false };
        var vm = new OperationViewModel(new FakeCommandService(), gate);
        vm.ApplyConnectionState(ConnectionState.Disconnected);
        vm.ApplySnapshot(ManualStopped());

        Assert.False(vm.IsOnline);
        Assert.False(vm.StartCommand.CanExecute(null));
        Assert.False(vm.StopCommand.CanExecute(null));
        Assert.False(vm.ResetCommand.CanExecute(null));
        Assert.False(vm.EStopRequestCommand.CanExecute(null));
        Assert.False(vm.AutoModeCommand.CanExecute(null));
        Assert.False(vm.ManualModeCommand.CanExecute(null));
        Assert.False(vm.BypassModeCommand.CanExecute(null));
    }

    /// <summary>Documented decision: 启动 (M101) is a manual-initiated run, so it needs Manual + stopped + no fault.</summary>
    [Fact]
    public void Start_requires_manual_stopped_no_fault()
    {
        var (_, _, vm) = Online();

        // Auto mode & running: no start.
        vm.ApplySnapshot(new DeviceSnapshot(Snap(("M2", true), ("M3", true), ("D110", (ushort)0)), DateTime.UtcNow));
        Assert.False(vm.StartCommand.CanExecute(null));

        // Manual but running: no start.
        vm.ApplySnapshot(new DeviceSnapshot(Snap(("M1", true), ("M3", true), ("D110", (ushort)0)), DateTime.UtcNow));
        Assert.False(vm.StartCommand.CanExecute(null));

        // Manual, stopped, but faulted: no start.
        vm.ApplySnapshot(new DeviceSnapshot(Snap(("M1", true), ("M3", false), ("D110", (ushort)3)), DateTime.UtcNow));
        Assert.False(vm.StartCommand.CanExecute(null));

        // Manual, stopped, no fault: start allowed (the PLC does the final interlock).
        vm.ApplySnapshot(ManualStopped());
        Assert.True(vm.StartCommand.CanExecute(null));
    }

    /// <summary>Documented decision: 停止 (M102) is always available while online, whatever the mode/run/fault.</summary>
    [Fact]
    public void Stop_is_always_available_online()
    {
        var (_, _, vm) = Online();

        vm.ApplySnapshot(new DeviceSnapshot(Snap(("M2", true), ("M3", true), ("D110", (ushort)5)), DateTime.UtcNow));
        Assert.True(vm.StopCommand.CanExecute(null));

        vm.ApplySnapshot(ManualStopped());
        Assert.True(vm.StopCommand.CanExecute(null));
    }

    /// <summary>Documented decision: 复位 (M103) needs the machine stopped (it clears a fault/latch); the fault state itself does not block it.</summary>
    [Fact]
    public void Reset_requires_stopped_but_allows_fault()
    {
        var (_, _, vm) = Online();

        // Running even with a fault: no reset.
        vm.ApplySnapshot(new DeviceSnapshot(Snap(("M1", true), ("M3", true), ("D110", (ushort)3)), DateTime.UtcNow));
        Assert.False(vm.ResetCommand.CanExecute(null));

        // Stopped with a fault: reset allowed (that is its job).
        vm.ApplySnapshot(new DeviceSnapshot(Snap(("M1", true), ("M3", false), ("D110", (ushort)3)), DateTime.UtcNow));
        Assert.True(vm.ResetCommand.CanExecute(null));
    }

    /// <summary>Documented decision: 急停请求 (M100) is a software stop request, available whenever online.</summary>
    [Fact]
    public void EStop_request_is_available_online()
    {
        var (_, _, vm) = Online();

        vm.ApplySnapshot(new DeviceSnapshot(Snap(("M2", true), ("M3", true), ("D110", (ushort)0)), DateTime.UtcNow));
        Assert.True(vm.EStopRequestCommand.CanExecute(null));

        vm.ApplySnapshot(ManualStopped());
        Assert.True(vm.EStopRequestCommand.CanExecute(null));
    }

    /// <summary>Documented decision: mode switches need a stopped machine; auto/bypass also need no fault.</summary>
    [Fact]
    public void Mode_commands_require_stopped_no_fault_for_auto_bypass()
    {
        var (_, _, vm) = Online();

        // Running: no mode switch at all.
        vm.ApplySnapshot(new DeviceSnapshot(Snap(("M1", true), ("M3", true), ("D110", (ushort)0)), DateTime.UtcNow));
        Assert.False(vm.AutoModeCommand.CanExecute(null));
        Assert.False(vm.BypassModeCommand.CanExecute(null));
        Assert.False(vm.ManualModeCommand.CanExecute(null));

        // Stopped, no fault: all mode switches allowed (idempotent re-issue is harmless).
        vm.ApplySnapshot(new DeviceSnapshot(Snap(("M2", true), ("M3", false), ("D110", (ushort)0)), DateTime.UtcNow));
        Assert.True(vm.AutoModeCommand.CanExecute(null));
        Assert.True(vm.BypassModeCommand.CanExecute(null));
        Assert.True(vm.ManualModeCommand.CanExecute(null));

        // Stopped but faulted: auto/bypass blocked (safety), manual still allowed so the operator can recover.
        vm.ApplySnapshot(new DeviceSnapshot(Snap(("M2", true), ("M3", false), ("D110", (ushort)2)), DateTime.UtcNow));
        Assert.False(vm.AutoModeCommand.CanExecute(null));
        Assert.False(vm.BypassModeCommand.CanExecute(null));
        Assert.True(vm.ManualModeCommand.CanExecute(null));
    }

    // --- Mode switch composition (design §4.4: 手动 M104=0,M105=0 / 自动 M104=1,M105=0 / 直通 M104=0,M105=1) ---

    [Fact]
    public async Task AutoMode_writes_m104_1_m105_0()
    {
        var (_, service, vm) = Online();
        vm.ApplySnapshot(ManualStopped());

        await vm.AutoModeCommand.ExecuteAsync(null);

        Assert.Collection(service.Requests,
            r => Assert.Equal((CommandTarget.AutoMode, true), (r.Target, r.Value)),
            r => Assert.Equal((CommandTarget.BypassMode, false), (r.Target, r.Value)));
    }

    [Fact]
    public async Task BypassMode_writes_m104_0_m105_1()
    {
        var (_, service, vm) = Online();
        vm.ApplySnapshot(ManualStopped());

        await vm.BypassModeCommand.ExecuteAsync(null);

        Assert.Collection(service.Requests,
            r => Assert.Equal((CommandTarget.AutoMode, false), (r.Target, r.Value)),
            r => Assert.Equal((CommandTarget.BypassMode, true), (r.Target, r.Value)));
    }

    [Fact]
    public async Task ManualMode_writes_m104_0_m105_0()
    {
        var (_, service, vm) = Online();
        vm.ApplySnapshot(ManualStopped());

        await vm.ManualModeCommand.ExecuteAsync(null);

        Assert.Collection(service.Requests,
            r => Assert.Equal((CommandTarget.AutoMode, false), (r.Target, r.Value)),
            r => Assert.Equal((CommandTarget.BypassMode, false), (r.Target, r.Value)));
    }

    // --- Mode confirmation via PLC read-back, NOT the write result (design §4.4) --------------------

    [Fact]
    public async Task Auto_mode_shows_switched_only_after_plc_snapshot_confirms()
    {
        var (_, _, vm) = Online();
        vm.ApplySnapshot(ManualStopped());
        Assert.Equal(MachineMode.Manual, vm.Mode);
        Assert.False(vm.IsModeSwitchPending);

        await vm.AutoModeCommand.ExecuteAsync(null);

        // The M104/M105 writes succeeded, but the machine is NOT yet reported in Auto: the displayed mode
        // must stay Manual and the switch is recorded as pending until a snapshot confirms M1/M2/M13.
        Assert.Equal(MachineMode.Manual, vm.Mode);
        Assert.Equal(MachineMode.Auto, vm.PendingMode);
        Assert.True(vm.IsModeSwitchPending);

        // PLC confirms Auto via M2.
        vm.ApplySnapshot(new DeviceSnapshot(Snap(("M2", true), ("M3", false), ("D110", (ushort)0)), DateTime.UtcNow));

        Assert.Equal(MachineMode.Auto, vm.Mode);
        Assert.Null(vm.PendingMode);
        Assert.False(vm.IsModeSwitchPending);
        Assert.Contains("成功", vm.CommandFeedbackText);
    }

    [Fact]
    public async Task Auto_mode_refused_by_plc_clears_pending_and_keeps_old_mode()
    {
        var (_, _, vm) = Online();
        vm.ApplySnapshot(ManualStopped());

        await vm.AutoModeCommand.ExecuteAsync(null);
        Assert.True(vm.IsModeSwitchPending);

        // The PLC stays in Manual (M1) on a snapshot that is at least one poll cycle NEWER than the write
        // completion — it had time to switch but did not, so this is a refusal: the pending flag resolves
        // against the confirmed state and the old mode is kept.
        vm.ApplySnapshot(new DeviceSnapshot(Snap(("M1", true), ("M3", false), ("D110", (ushort)0)),
            DateTime.UtcNow.AddSeconds(1)));

        Assert.Equal(MachineMode.Manual, vm.Mode);
        Assert.Null(vm.PendingMode);
        Assert.False(vm.IsModeSwitchPending);
        Assert.Contains("拒绝", vm.CommandFeedbackText);
    }

    [Fact]
    public async Task Pre_write_snapshot_with_different_mode_keeps_pending()
    {
        var (_, _, vm) = Online();
        vm.ApplySnapshot(ManualStopped());

        await vm.AutoModeCommand.ExecuteAsync(null);
        Assert.True(vm.IsModeSwitchPending);

        // A snapshot stamped BEFORE the write completed still reports the old mode (M1). At a 250ms poll
        // cadence this is a pre-write stale read, NOT a refusal — the switch may still be in flight, so the
        // pending flag must be kept.
        vm.ApplySnapshot(new DeviceSnapshot(Snap(("M1", true), ("M3", false), ("D110", (ushort)0)),
            DateTime.UtcNow.AddSeconds(-5)));

        Assert.Equal(MachineMode.Manual, vm.Mode);
        Assert.Equal(MachineMode.Auto, vm.PendingMode);
        Assert.True(vm.IsModeSwitchPending);
    }

    [Fact]
    public async Task Auto_mode_pending_survives_an_unknown_snapshot()
    {
        var (_, _, vm) = Online();
        vm.ApplySnapshot(ManualStopped());

        await vm.AutoModeCommand.ExecuteAsync(null);
        Assert.True(vm.IsModeSwitchPending);

        // A snapshot with no mode bit is "unknown" — the switch may still be in flight, so keep pending.
        vm.ApplySnapshot(new DeviceSnapshot(Snap(("M3", false), ("D110", (ushort)0)), DateTime.UtcNow));

        Assert.Equal(MachineMode.Unknown, vm.Mode);
        Assert.True(vm.IsModeSwitchPending);
    }

    [Fact]
    public async Task Failed_mode_write_does_not_pend_any_mode()
    {
        var (_, service, vm) = Online();
        // The FIRST write (M104=1) never landed — the combo cannot have taken, so nothing can be confirmed.
        service.Handler = r => r == new CommandRequest(CommandTarget.AutoMode, true)
            ? new CommandResult(CommandStatus.Unknown, r.Target, "timeout")
            : new CommandResult(CommandStatus.Success, r.Target);
        vm.ApplySnapshot(ManualStopped());

        await vm.AutoModeCommand.ExecuteAsync(null);

        // A non-success first write cannot confirm the switch, so nothing is pended and the mode stays as reported.
        Assert.Null(vm.PendingMode);
        Assert.False(vm.IsModeSwitchPending);
    }

    [Fact]
    public async Task Partial_mode_combo_with_unknown_second_write_still_pends()
    {
        var (_, service, vm) = Online();
        // First (M104=1) write lands; second (M105=0) is unknown — the mutually-exclusive combo may have
        // half-applied at the PLC, so the switch outcome must be arbitrated by the snapshot read-back.
        service.Handler = r => r == new CommandRequest(CommandTarget.BypassMode, false)
            ? new CommandResult(CommandStatus.Unknown, r.Target, "timeout")
            : new CommandResult(CommandStatus.Success, r.Target);
        vm.ApplySnapshot(ManualStopped());

        await vm.AutoModeCommand.ExecuteAsync(null);

        // Best-effort: pend the requested mode so the snapshot read-back resolves the half combo.
        Assert.Equal(MachineMode.Auto, vm.PendingMode);
        Assert.True(vm.IsModeSwitchPending);
        Assert.Equal("自动模式已请求，等待PLC确认", vm.CommandFeedbackText);

        // The PLC never reached Auto → a fresh-enough (≥ one poll cycle newer) snapshot refuses the switch.
        vm.ApplySnapshot(new DeviceSnapshot(Snap(("M1", true), ("M3", false), ("D110", (ushort)0)),
            DateTime.UtcNow.AddSeconds(1)));

        Assert.Null(vm.PendingMode);
        Assert.False(vm.IsModeSwitchPending);
    }

    // --- Mode switch cross-command guard (design §6.3): no interleaved M104/M105 combos ---------------

    [Fact]
    public async Task Pending_mode_switch_disables_other_mode_commands()
    {
        var (_, _, vm) = Online();
        vm.ApplySnapshot(ManualStopped());

        await vm.AutoModeCommand.ExecuteAsync(null);
        Assert.True(vm.IsModeSwitchPending);

        // While one mode switch is in flight, the other mode commands must be disabled so a rapid
        // Auto→Manual double-submit cannot interleave 4 coil writes into a mixed M104/M105 combo.
        Assert.False(vm.AutoModeCommand.CanExecute(null));
        Assert.False(vm.ManualModeCommand.CanExecute(null));
        Assert.False(vm.BypassModeCommand.CanExecute(null));

        // The PLC confirms Auto; the pending clears and the other mode switches re-enable.
        vm.ApplySnapshot(new DeviceSnapshot(Snap(("M2", true), ("M3", false), ("D110", (ushort)0)), DateTime.UtcNow));
        Assert.False(vm.IsModeSwitchPending);
        Assert.True(vm.AutoModeCommand.CanExecute(null));
        Assert.True(vm.ManualModeCommand.CanExecute(null));
        Assert.True(vm.BypassModeCommand.CanExecute(null));
    }

    // --- E-stop request is clearly a SOFTWARE request (design §4.4: 仅为软件停机请求) -----------------

    [Fact]
    public void EStop_request_is_presented_as_a_software_request()
    {
        var vm = new OperationViewModel(new FakeCommandService(), new FakeCommandGate());

        Assert.Equal("软件急停请求", vm.EStopRequestText);
        Assert.Contains("非物理", vm.EStopRequestHint);
    }

    [Fact]
    public async Task EStop_request_executes_m100_pulse()
    {
        var (_, service, vm) = Online();

        await vm.EStopRequestCommand.ExecuteAsync(null);

        var request = Assert.Single(service.Requests);
        Assert.Equal(CommandTarget.EStopRequest, request.Target);
        Assert.True(request.Value); // pulse always writes true.
        Assert.Equal("急停请求已下发", vm.CommandFeedbackText);
    }

    // --- Result feedback (design §6.3 结果反馈) ----------------------------------------------------

    [Fact]
    public async Task Pulse_success_records_positive_feedback()
    {
        var (_, _, vm) = Online();

        await vm.StartCommand.ExecuteAsync(null);

        Assert.Equal("启动命令已下发", vm.CommandFeedbackText);
    }

    [Fact]
    public async Task Pulse_unknown_reports_status_unknown()
    {
        var (_, service, vm) = Online();
        service.Handler = _ => new CommandResult(CommandStatus.Unknown, CommandTarget.Start, "transport timeout");

        await vm.StartCommand.ExecuteAsync(null);

        Assert.Equal("启动命令状态未知：transport timeout", vm.CommandFeedbackText);
    }

    [Fact]
    public async Task Pulse_rejected_reports_denial()
    {
        var (_, service, vm) = Online();
        service.Handler = _ => new CommandResult(CommandStatus.Rejected, CommandTarget.Stop, "gate: not manual-idle");

        await vm.StopCommand.ExecuteAsync(null);

        Assert.Equal("停止命令被拒绝：gate: not manual-idle", vm.CommandFeedbackText);
    }

    /// <summary>Exceptions from the command service must not escape the AsyncRelayCommand (they would surface
    /// on the UI thread): they are captured and reported on the feedback line, keeping the VM alive.</summary>
    [Fact]
    public async Task Command_exception_sets_failure_feedback_and_keeps_vm_alive()
    {
        var (_, service, vm) = Online();
        service.Handler = _ => throw new InvalidOperationException("transport failure");

        // Must not rethrow.
        await vm.StartCommand.ExecuteAsync(null);

        Assert.StartsWith("命令失败：", vm.CommandFeedbackText);
        Assert.Contains("transport failure", vm.CommandFeedbackText);
        Assert.True(vm.IsOnline); // the VM stays usable (online gate unchanged).
    }

    [Fact]
    public async Task Bypass_mode_shows_switched_only_after_plc_snapshot_confirms()
    {
        var (_, _, vm) = Online();
        vm.ApplySnapshot(ManualStopped());

        await vm.BypassModeCommand.ExecuteAsync(null);
        Assert.Equal(MachineMode.Bypass, vm.PendingMode);
        Assert.True(vm.IsModeSwitchPending);

        // PLC confirms Bypass via M13.
        vm.ApplySnapshot(new DeviceSnapshot(Snap(("M13", true), ("M3", false), ("D110", (ushort)0)), DateTime.UtcNow));

        Assert.Equal(MachineMode.Bypass, vm.Mode);
        Assert.Null(vm.PendingMode);
        Assert.False(vm.IsModeSwitchPending);
        Assert.Contains("成功", vm.CommandFeedbackText);
    }

    /// <summary>Read-only <see cref="ICommandGate"/> the tests control directly.</summary>
    private sealed class FakeCommandGate : ICommandGate
    {
        public bool IsOnline { get; set; }
        public bool IsManualIdle { get; set; }
    }

    /// <summary>Records executed <see cref="CommandRequest"/>s and returns a configurable result.</summary>
    private sealed class FakeCommandService : ICommandService
    {
        public List<CommandRequest> Requests { get; } = new();
        public Func<CommandRequest, CommandResult>? Handler { get; set; }

        public Task<CommandResult> ExecuteAsync(CommandRequest request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(Handler is null
                ? new CommandResult(CommandStatus.Success, request.Target)
                : Handler(request));
        }

        public Task ReleaseJogCommandsAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
