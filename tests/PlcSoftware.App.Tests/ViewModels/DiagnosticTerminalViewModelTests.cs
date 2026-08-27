using PlcSoftware.App.ViewModels;
using PlcSoftware.Core.Abstractions;
using PlcSoftware.Core.Models;
using PlcSoftware.Core.Services;

namespace PlcSoftware.App.Tests.ViewModels;

/// <summary>
/// Pins how <see cref="DiagnosticTerminalViewModel"/> drives the Modbus debug terminal page (design §6.5):
/// FC01/02/03/04 reads and FC05/06 single-point writes over the injected
/// <see cref="DiagnosticTerminalService"/>, with the unlock/lock gate, the machine-running write rejection,
/// the result feedback (hex + elapsed + status/error) and the no-throw guarantee.
///
/// <para><b>Reads (always permitted).</b> Parsing an invalid slave/address/count lands on
/// <see cref="DiagnosticTerminalViewModel.StatusText"/>/<see cref="DiagnosticTerminalViewModel.ErrorText"/>
/// without reaching the client; a valid read routes to the service and surfaces the hex/elapsed on success.</para>
///
/// <para><b>Writes (locked + stop-gated + per-write confirmation, design §6.9).</b>
/// <see cref="DiagnosticTerminalViewModel.WriteRegisterCommand"/> /
/// <see cref="DiagnosticTerminalViewModel.WriteCoilCommand"/> open only while the terminal is unlocked AND the
/// link is online (<see cref="DiagnosticTerminalViewModel.IsOnline"/>). They now only <em>stage</em> a write:
/// the input is validated and the operation is set pending (<see cref="DiagnosticTerminalViewModel.IsPending"/> +
/// <see cref="DiagnosticTerminalViewModel.ConfirmationText"/>) without reaching the client. The write runs only
/// after the operator confirms via <see cref="DiagnosticTerminalViewModel.ConfirmWriteCommand"/>, and a pending
/// write is dropped via <see cref="DiagnosticTerminalViewModel.CancelWriteCommand"/>. A locked terminal (or a
/// machine that is running, via the service's <c>isRunningProvider</c>) rejects the write and reports the
/// reason — never throws.</para>
///
/// <para><b>No WPF dependency.</b> The view model consumes <see cref="ConnectionState"/> through
/// <see cref="DiagnosticTerminalViewModel.ApplyConnectionState"/> and executes everything through the injected
/// <see cref="DiagnosticTerminalService"/>. The suite is WPF-runtime-free: it CANNOT run on the WSL/Linux
/// cross-build (WindowsDesktop runtime absent) — on Linux it only contributes a compile RED/GREEN check; full
/// execution (GREEN) happens on the Windows CI runner.</para>
/// </summary>
public class DiagnosticTerminalViewModelTests
{
    private static readonly CancellationToken None = CancellationToken.None;

    /// <summary>Builds a VM over a real <see cref="DiagnosticTerminalService"/>, a recording Modbus fake and a
    /// controllable gate (default online + unlocked).</summary>
    private static (FakeGate Gate, FakeModbusClient Client, DiagnosticTerminalViewModel Vm) Build(
        bool online = true, bool unlocked = true, Func<bool>? isRunning = null)
    {
        var gate = new FakeGate { IsOnline = online };
        var client = new FakeModbusClient();
        var service = new DiagnosticTerminalService(client, isRunningProvider: isRunning ?? (() => false));
        if (unlocked)
        {
            service.SetUnlocked(true);
        }

        var vm = new DiagnosticTerminalViewModel(service, gate);
        vm.ApplyConnectionState(online ? ConnectionState.Online : ConnectionState.Disconnected);
        vm.SlaveId = "1";
        vm.Address = "0";
        vm.Count = "1";
        vm.Value = "1";
        return (gate, client, vm);
    }

    // --- Unlock flow (design §6.5: 解锁后才允许写入) --------------------------------------------------

    [Fact]
    public void Unlock_command_grants_the_write_unlock()
    {
        var (_, _, vm) = Build(unlocked: false);
        Assert.False(vm.IsUnlocked);

        vm.UnlockCommand.Execute(null);

        Assert.True(vm.IsUnlocked);
        Assert.Contains("已解锁", vm.StatusText);
        Assert.Null(vm.ErrorText);
    }

    [Fact]
    public void Lock_command_revokes_the_write_unlock()
    {
        var (_, _, vm) = Build(unlocked: true);
        Assert.True(vm.IsUnlocked);

        vm.LockCommand.Execute(null);

        Assert.False(vm.IsUnlocked);
        Assert.Contains("已锁定", vm.StatusText);
    }

    // --- Per-write confirmation (design §6.9 每次写入确认) -----------------------------------------

    [Fact]
    public void Staging_a_register_write_sets_pending_without_writing()
    {
        var (_, client, vm) = Build();
        vm.Value = "250";

        vm.WriteRegisterCommand.Execute(null);

        Assert.True(vm.IsPending);
        Assert.NotNull(vm.ConfirmationText);
        Assert.Contains("FC06", vm.ConfirmationText);
        Assert.Contains("从站1", vm.ConfirmationText);
        Assert.Contains("地址0", vm.ConfirmationText);
        Assert.Contains("值250", vm.ConfirmationText);
        Assert.Empty(client.Writes); // no write ran at stage time.
    }

    [Fact]
    public void Staging_a_coil_write_sets_pending_without_writing()
    {
        var (_, client, vm) = Build();
        vm.Value = "true";

        vm.WriteCoilCommand.Execute(null);

        Assert.True(vm.IsPending);
        Assert.NotNull(vm.ConfirmationText);
        Assert.Contains("FC05", vm.ConfirmationText);
        Assert.Empty(client.Writes);
    }

    [Fact]
    public async Task Confirm_write_runs_the_registered_write()
    {
        var (_, client, vm) = Build();
        vm.Value = "250";
        vm.WriteRegisterCommand.Execute(null);
        Assert.True(vm.IsPending);

        await vm.ConfirmWriteCommand.ExecuteAsync(null);

        Assert.Equal((ushort)250, client.Writes.Single().Value);
        Assert.False(vm.IsPending);
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public async Task Confirm_coil_write_runs_the_coil_write()
    {
        var (_, client, vm) = Build();
        vm.Value = "true";
        vm.WriteCoilCommand.Execute(null);

        await vm.ConfirmWriteCommand.ExecuteAsync(null);

        Assert.Single(client.Writes);
        Assert.False(vm.IsPending);
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public void Cancel_clears_pending_without_writing()
    {
        var (_, client, vm) = Build();
        vm.Value = "250";
        vm.WriteRegisterCommand.Execute(null);
        Assert.True(vm.IsPending);

        vm.CancelWriteCommand.Execute(null);

        Assert.False(vm.IsPending);
        Assert.Null(vm.ConfirmationText);
        Assert.Empty(client.Writes);
    }

    [Fact]
    public void Editing_a_new_value_while_pending_replaces_the_confirmation()
    {
        var (_, _, vm) = Build();
        vm.Value = "250";
        vm.WriteRegisterCommand.Execute(null);
        Assert.Contains("值250", vm.ConfirmationText);

        vm.Value = "300";
        vm.WriteRegisterCommand.Execute(null);

        Assert.Contains("值300", vm.ConfirmationText);
        Assert.True(vm.IsPending);
    }

    // --- Running machine rejects a write (design §6.5: 机器运行时禁止写入) ---------------------------

    [Fact]
    public async Task Confirm_write_while_machine_running_is_rejected_through_the_service()
    {
        // The gate reports online + unlocked; the service's running provider rejects the write.
        var (_, _, vm) = Build(online: true, unlocked: true, isRunning: () => true);
        vm.WriteRegisterCommand.Execute(null);

        await vm.ConfirmWriteCommand.ExecuteAsync(null);

        Assert.False(vm.IsBusy);
        Assert.Contains("失败", vm.StatusText);
        Assert.Contains("running", vm.ErrorText, StringComparison.OrdinalIgnoreCase);
    }

    // --- Write commands are CanExecute-gated by online + unlocked -----------------------------------

    [Fact]
    public void Write_commands_are_disabled_when_locked()
    {
        var (_, _, vm) = Build(unlocked: false);
        Assert.False(vm.IsUnlocked);
        Assert.False(vm.WriteRegisterCommand.CanExecute(null));
        Assert.False(vm.WriteCoilCommand.CanExecute(null));
    }

    [Fact]
    public void Write_commands_are_disabled_offline()
    {
        var (_, _, vm) = Build(online: false, unlocked: true);
        Assert.False(vm.IsOnline);
        Assert.False(vm.WriteRegisterCommand.CanExecute(null));
        Assert.False(vm.WriteCoilCommand.CanExecute(null));
    }

    [Fact]
    public void Write_commands_are_enabled_when_online_and_unlocked()
    {
        var (_, _, vm) = Build(online: true, unlocked: true);
        Assert.True(vm.WriteRegisterCommand.CanExecute(null));
        Assert.True(vm.WriteCoilCommand.CanExecute(null));
    }

    [Fact]
    public void Read_command_is_always_enabled_when_online()
    {
        var (_, _, vm) = Build(online: true, unlocked: false); // reads need no unlock.
        Assert.True(vm.RunReadCommand.CanExecute(null));
    }

    // --- Result feedback: hex + elapsed + status (design §6.5: 响应耗时、十六进制显示) -----------------

    [Fact]
    public async Task Successful_read_shows_hex_elapsed_and_status()
    {
        var (_, client, vm) = Build();
        client.ReadHolding = new[] { (ushort)0x1234 };
        vm.Count = "1";

        await vm.RunReadCommand.ExecuteAsync(null);

        Assert.Contains("读取完成", vm.StatusText);
        Assert.Contains("0x1234", vm.HexResult);
        Assert.False(string.IsNullOrWhiteSpace(vm.ElapsedMs));
        Assert.Null(vm.ErrorText);
    }

    [Fact]
    public async Task Failed_read_surfaces_error_without_throwing()
    {
        var (_, _, vm) = Build();
        vm.SlaveId = "999"; // out of the 1..247 bound the service enforces.

        await vm.RunReadCommand.ExecuteAsync(null);

        Assert.Contains("失败", vm.StatusText);
        Assert.False(string.IsNullOrWhiteSpace(vm.ErrorText));
        Assert.False(vm.IsBusy);
    }

    // --- No-throw on invalid inputs (design §6.5: 参数解析失败不抛出) ---------------------------------

    [Fact]
    public async Task Invalid_slave_id_input_is_reported_without_a_write()
    {
        var (_, client, vm) = Build();
        vm.SlaveId = "abc";

        await vm.RunReadCommand.ExecuteAsync(null);

        Assert.Contains("站号", vm.ErrorText);
        Assert.Equal(0, client.ReadHoldingCalls);
    }

    [Fact]
    public void Invalid_write_value_is_reported_without_staging_a_write()
    {
        var (_, client, vm) = Build();
        vm.Value = "not-a-number";

        vm.WriteRegisterCommand.Execute(null);

        Assert.Contains("寄存器值", vm.ErrorText);
        Assert.False(vm.IsPending);
        Assert.Empty(client.Writes);
    }

    // --- Recording fakes (test-local) ------------------------------------

    private sealed class FakeGate : ICommandGate
    {
        public bool IsOnline { get; set; }
        public bool IsManualIdle { get; set; } = true;
    }

    private sealed class FakeModbusClient : IModbusClient
    {
        public List<(ushort Address, ushort Value)> Writes { get; } = new();
        public int ReadHoldingCalls { get; private set; }
        public ushort[]? ReadHolding { get; set; } = Array.Empty<ushort>();

        public Task<ushort[]> ReadHoldingRegistersAsync(byte slaveId, ushort address, ushort count, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ReadHoldingCalls++;
            return Task.FromResult(ReadHolding ?? new ushort[count]);
        }

        public Task WriteSingleRegisterAsync(byte slaveId, ushort address, ushort value, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Writes.Add((address, value));
            return Task.CompletedTask;
        }

        public Task<bool[]> ReadCoilsAsync(byte slaveId, ushort address, ushort count, CancellationToken cancellationToken)
            => Task.FromResult(new bool[count]);

        public Task<bool[]> ReadDiscreteInputsAsync(byte slaveId, ushort address, ushort count, CancellationToken cancellationToken)
            => Task.FromResult(new bool[count]);

        public Task<ushort[]> ReadInputRegistersAsync(byte slaveId, ushort address, ushort count, CancellationToken cancellationToken)
            => Task.FromResult(new ushort[count]);

        public Task WriteSingleCoilAsync(byte slaveId, ushort address, bool value, CancellationToken cancellationToken)
            => Task.CompletedTask;

        public Task ConnectAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task DisconnectAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
