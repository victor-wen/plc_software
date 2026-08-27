using PlcSoftware.App.ViewModels;
using PlcSoftware.Core.Abstractions;
using PlcSoftware.Core.Models;
using PlcSoftware.Core.Services;

namespace PlcSoftware.App.Tests.ViewModels;

/// <summary>
/// Pins how <see cref="ParametersViewModel"/> drives the parameter page (design §6.5): the editable
/// D201/D202/D204/D205 write flow — validate integer input → show old/new/unit/range → confirm →
/// <see cref="ParameterService"/>(write + read-back) → report the result including Mismatch / Unknown —
/// plus the read-only D203/D210/D212.D213 display, the offline gate and the save-in-progress
/// duplicate-click guard.
///
/// <para><b>Write flow (design §6.5).</b> A non-integer input is rejected with an error and no write;
/// an out-of-range value (vs the configured Min/Max) is rejected with the allowed range; a valid value
/// stages the confirmation prompt (旧值 → 新值 + 单位 + 允许范围) and only after the operator confirms is the
/// value written. The write goes through the injected <see cref="ParameterService"/>, so it carries the
/// FC06-write-then-FC03-read-back verification of design §5.3 (参数写入后必须读回一致才报告成功): a matching
/// read-back reports success, a differing read-back reports a mismatch (原值保留 + 原因), and a communication
/// interruption reports unknown without crashing. Because <see cref="ParameterEditor.OldValue"/> is driven
/// only by the snapshot and never by a write, a failed write always retains the original value.</para>
///
/// <para><b>Save-in-progress guard.</b> While a write is in flight <see cref="ParametersViewModel.IsSaving"/>
/// is <c>true</c> and the confirm command's <c>CanExecute</c> is <c>false</c>, so a rapid double-click cannot
/// fire two writes.</para>
///
/// <para><b>No WPF dependency.</b> The view model consumes Core snapshots + <see cref="ConnectionState"/>
/// through <see cref="ParametersViewModel.ApplySnapshot"/> / <see cref="ParametersViewModel.ApplyConnectionState"/>
/// and executes writes through the injected <see cref="ParameterService"/>. The suite is WPF-runtime-free: it
/// CANNOT run on the WSL/Linux cross-build (WindowsDesktop runtime absent) — on Linux it only contributes a
/// compile RED/GREEN check; full execution (GREEN) happens on the Windows CI runner.</para>
/// </summary>
public class ParametersViewModelTests
{
    private static IReadOnlyDictionary<string, object?> Snap(params (string Key, object? Value)[] values)
        => values.ToDictionary(v => v.Key, v => v.Value);

    /// <summary>A live process snapshot: every editable parameter plus the read-only registers.</summary>
    private static DeviceSnapshot LiveSnapshot()
        => new DeviceSnapshot(
            Snap(("D201", (ushort)250), ("D202", (ushort)1200), ("D204", (ushort)50),
                ("D205", (ushort)200), ("D203", (ushort)1200), ("D210", (ushort)5),
                (RegisterDecoder.WidthPulseCountKey, 123456u)),
            DateTime.UtcNow);

    /// <summary>Builds an online VM over a real <see cref="ParameterService"/>, a recording Modbus fake and a
    /// controllable gate (the D201 editor is index 0, matching the App wiring order).</summary>
    private static (FakeGate Gate, FakeModbusClient Client, ParametersViewModel Vm) Build(bool online = true)
    {
        var gate = new FakeGate { IsOnline = online };
        var client = new FakeModbusClient();
        var vm = new ParametersViewModel(new ParameterService(client, gate, Writable()), gate, Writable());
        vm.ApplyConnectionState(online ? ConnectionState.Online : ConnectionState.Disconnected);
        return (gate, client, vm);
    }

    // --- Integer input + range hint + range validation (design §6.5 整数输入, 允许范围) ------------------

    [Fact]
    public void Non_integer_input_is_rejected()
    {
        var (_, _, vm) = Build();
        var d201 = vm.WritableParameters[0];
        d201.InputText = "abc";

        vm.PrepareWriteCommand.Execute(d201);

        Assert.Equal("请输入整数。", d201.Error);
        Assert.False(d201.IsPending);
        Assert.False(vm.IsPending);
    }

    [Fact]
    public void Out_of_range_input_is_rejected_with_the_allowed_range()
    {
        var (_, _, vm) = Build();
        var d201 = vm.WritableParameters[0]; // D201 range is 10 ~ 500.
        d201.InputText = "600";

        vm.PrepareWriteCommand.Execute(d201);

        Assert.Contains("超出允许范围", d201.Error);
        Assert.Contains("10 ~ 500", d201.Error);
        Assert.False(d201.IsPending);
    }

    [Fact]
    public void Range_hint_text_shows_configured_limits_and_unit()
    {
        var (_, _, vm) = Build();
        Assert.Equal("10 ~ 500 Hz", vm.WritableParameters[0].RangeHintText);
        Assert.Equal("100 ~ 1500 mm", vm.WritableParameters[1].RangeHintText);
    }

    [Fact]
    public void Unconfigured_range_is_refused_clearly_at_prepare_write()
    {
        // A definition with no configured Min/Max (工程配置上下限未配置) is refused at the VM (design §4.3:
        // 上下限未配置或配置非法时禁止写入) — clearer than deferring it to the injected service.
        var gate = new FakeGate { IsOnline = true };
        var client = new FakeModbusClient();
        var definitions = new[] { Def("D201", 101, "Hz", null, null) };
        var vm = new ParametersViewModel(new ParameterService(client, gate, definitions), gate, definitions);
        vm.ApplyConnectionState(ConnectionState.Online);
        var d201 = vm.WritableParameters[0];
        d201.InputText = "300";

        vm.PrepareWriteCommand.Execute(d201);

        Assert.Contains("未配置范围", d201.Error);
        Assert.False(d201.IsPending);
        Assert.False(vm.IsPending);
        Assert.Empty(client.Writes); // nothing reached the client.
    }

    [Fact]
    public void Editing_the_input_clears_the_previous_validation_error()
    {
        var (_, _, vm) = Build();
        var d201 = vm.WritableParameters[0];
        d201.InputText = "abc";
        vm.PrepareWriteCommand.Execute(d201);
        Assert.Equal("请输入整数。", d201.Error);

        // Re-typing a value clears the stale error (design §6.5: 输入即清空错误提示).
        d201.InputText = "100";
        Assert.Null(d201.Error);
    }

    // --- Confirmation prompt (design §6.5: 写入前显示旧值、新值、单位和允许范围) --------------------------

    [Fact]
    public void Valid_input_stages_confirmation_with_old_new_unit_range()
    {
        var (_, _, vm) = Build();
        var d201 = vm.WritableParameters[0];
        vm.ApplySnapshot(LiveSnapshot()); // D201 old value = 250.
        d201.InputText = "300";

        vm.PrepareWriteCommand.Execute(d201);

        Assert.Null(d201.Error);
        Assert.True(d201.IsPending);
        Assert.True(vm.IsPending);
        Assert.Equal(300, d201.PendingValue);
        Assert.Contains("D201", d201.ConfirmationText);
        Assert.Contains("250", d201.ConfirmationText); // old value.
        Assert.Contains("300", d201.ConfirmationText); // new value.
        Assert.Contains("Hz", d201.ConfirmationText);  // unit.
        Assert.Contains("10 ~ 500", d201.ConfirmationText); // allowed range.
    }

    [Fact]
    public void Confirmation_is_cancelled_without_a_write()
    {
        var (_, client, vm) = Build();
        var d201 = vm.WritableParameters[0];
        d201.InputText = "300";
        vm.PrepareWriteCommand.Execute(d201);
        Assert.True(d201.IsPending);

        vm.CancelWriteCommand.Execute(null);

        Assert.False(d201.IsPending);
        Assert.False(vm.IsPending);
        Assert.Empty(client.Writes); // no write was attempted.
    }

    // --- Read-back result (design §5.3: 读回一致才报告成功; §6.5: 写回失败时保留原值并记录原因) ---------------

    [Fact]
    public async Task Confirm_write_matching_read_back_reports_success()
    {
        var (_, client, vm) = Build();
        var d201 = vm.WritableParameters[0];
        vm.ApplySnapshot(LiveSnapshot());
        d201.InputText = "300";
        vm.PrepareWriteCommand.Execute(d201);

        await vm.ConfirmWriteCommand.ExecuteAsync(null);

        // The write went to the D201 protocol address (101) and the read-back confirmed it.
        Assert.Equal(((ushort)101, (ushort)300), client.Writes.Single());
        Assert.Contains("成功", d201.ResultText);
        Assert.False(vm.IsSaving);
        Assert.False(vm.IsPending);
    }

    [Fact]
    public async Task Staging_a_new_confirmation_clears_a_previous_result()
    {
        var (_, _, vm) = Build();
        var d201 = vm.WritableParameters[0];
        vm.ApplySnapshot(LiveSnapshot());
        d201.InputText = "300";
        vm.PrepareWriteCommand.Execute(d201);
        await vm.ConfirmWriteCommand.ExecuteAsync(null);
        Assert.Contains("成功", d201.ResultText);

        // A new valid value stages a fresh confirmation, neutralising the previous read-back outcome.
        d201.InputText = "350";
        vm.PrepareWriteCommand.Execute(d201);

        Assert.Null(d201.ResultText);
        Assert.True(d201.IsPending);
    }

    [Fact]
    public async Task Confirm_write_read_back_mismatch_reports_mismatch_and_keeps_original()
    {
        var (_, client, vm) = Build();
        var d201 = vm.WritableParameters[0];
        vm.ApplySnapshot(LiveSnapshot()); // old 250.
        d201.InputText = "300";
        client.OverrideReadBack = 301; // the PLC read back a different value.
        vm.PrepareWriteCommand.Execute(d201);

        await vm.ConfirmWriteCommand.ExecuteAsync(null);

        Assert.Contains("不一致", d201.ResultText);
        Assert.Contains("已保留原值", d201.ResultText);
        // The write was still attempted (FC06), and the original (snapshot) value is retained on the editor.
        Assert.Equal(((ushort)101, (ushort)300), client.Writes.Single());
        Assert.Equal(250, d201.OldValue);
        Assert.False(vm.IsPending);
    }

    [Fact]
    public async Task Confirm_write_communication_interruption_reports_unknown()
    {
        var (_, client, vm) = Build();
        var d201 = vm.WritableParameters[0];
        d201.InputText = "300";
        client.ThrowOnReadBack = true; // a comms interruption leaves the outcome unverifiable.
        vm.PrepareWriteCommand.Execute(d201);

        await vm.ConfirmWriteCommand.ExecuteAsync(null);

        Assert.Contains("未知", d201.ResultText);
        Assert.Contains("已保留原值", d201.ResultText);
        Assert.Equal((ushort)101, client.Writes.Single().Address);
        Assert.False(vm.IsPending);
    }

    [Fact]
    public async Task Confirm_write_offline_is_rejected_and_keeps_original()
    {
        var (gate, _, vm) = Build();
        var d201 = vm.WritableParameters[0];
        vm.ApplySnapshot(LiveSnapshot()); // D201 old value = 250 (retained on a failed write).
        d201.InputText = "300";
        vm.PrepareWriteCommand.Execute(d201);
        Assert.True(d201.IsPending);

        // The link drops between staging and confirming; the service enforces §5.3 (禁止写入) even though the
        // UI gate would have disabled the confirm button. The English service reason is localised for the HMI.
        gate.IsOnline = false;

        await vm.ConfirmWriteCommand.ExecuteAsync(null);

        Assert.Contains("被拒绝", d201.ResultText);
        Assert.Contains("通信离线", d201.ResultText);
        Assert.Equal(250, d201.OldValue); // the original (snapshot) value survives the failed write.
        Assert.False(vm.IsPending);
    }

    [Fact]
    public void Confirm_write_disabled_when_the_link_drops_after_staging()
    {
        var (gate, _, vm) = Build();
        var d201 = vm.WritableParameters[0];
        d201.InputText = "300";
        vm.PrepareWriteCommand.Execute(d201);
        Assert.True(vm.ConfirmWriteCommand.CanExecute(null));

        // The link drops between staging and confirming; ApplyConnectionState re-queries the confirm
        // command's CanExecute, so the visible confirm button is disabled (design §5.3: 断线禁止写入).
        gate.IsOnline = false;
        vm.ApplyConnectionState(ConnectionState.Disconnected);

        Assert.False(vm.ConfirmWriteCommand.CanExecute(null));
    }

    // --- Save-in-progress prevents duplicate clicks (design §6.5 IsSaving guard) ---------------------

    [Fact]
    public async Task Saving_disables_confirm_and_cancel_until_the_write_completes()
    {
        var (_, client, vm) = Build();
        var d201 = vm.WritableParameters[0];
        vm.ApplySnapshot(LiveSnapshot());
        d201.InputText = "300";
        vm.PrepareWriteCommand.Execute(d201);

        client.HoldWrites = true;
        var inFlight = vm.ConfirmWriteCommand.ExecuteAsync(null);

        // While the write is in flight the Save (confirm) button is disabled: a second click cannot fire.
        Assert.True(vm.IsSaving);
        Assert.False(vm.ConfirmWriteCommand.CanExecute(null));
        Assert.False(vm.CancelWriteCommand.CanExecute(null));

        client.ReleaseHeldWrites();
        await inFlight;

        Assert.False(vm.IsSaving);
        Assert.Contains("成功", d201.ResultText);
    }

    // --- Read-only display (design §6.5: 显示 D203、D210、D212-D213) ----------------------------------

    [Fact]
    public void Read_only_parameters_display_snapshot_values()
    {
        var (_, _, vm) = Build();
        vm.ApplySnapshot(LiveSnapshot());

        Assert.Collection(vm.ReadOnlyParameters,
            p =>
            {
                Assert.Equal("D203", p.Key);
                Assert.Equal("当前宽度", p.Label);
                Assert.Equal("mm", p.Unit);
                Assert.Equal("1200", p.ValueText);
            },
            p =>
            {
                Assert.Equal("D210", p.Key);
                Assert.Equal("调宽差值", p.Label);
                Assert.Equal(string.Empty, p.Unit);
                Assert.Equal("5", p.ValueText);
            },
            p =>
            {
                Assert.Equal(RegisterDecoder.WidthPulseCountKey, p.Key); // "D212.D213" composite.
                Assert.Equal("调宽脉冲数", p.Label);
                Assert.Equal("脉冲", p.Unit);
                Assert.Equal("123456", p.ValueText); // the low-word-first UInt32.
            });
    }

    [Fact]
    public void Read_only_value_is_empty_until_the_snapshot_reports_it()
    {
        var (_, _, vm) = Build();
        Assert.All(vm.ReadOnlyParameters, p => Assert.Null(p.ValueText));
    }

    // --- Offline gate (design §5.3: 断线禁止写入) ---------------------------------------------------

    [Fact]
    public void Offline_disables_writes()
    {
        var (_, _, vm) = Build(online: false);
        var d201 = vm.WritableParameters[0];

        Assert.False(vm.IsOnline);
        Assert.False(vm.PrepareWriteCommand.CanExecute(d201));
    }

    /// <summary>Default writable set binding D201/D202/D204/D205 to their protocol addresses (design §4.3),
    /// matching the App wiring order.</summary>
    private static ParameterDefinition[] Writable()
        => new[]
        {
            Def("D201", 101, "Hz", 10, 500),
            Def("D202", 102, "mm", 100, 1500),
            Def("D204", 104, "脉冲/mm", 1, 1000),
            Def("D205", 105, "Hz", 10, 1000),
        };

    private static ParameterDefinition Def(string name, ushort address, string unit, int min, int max)
        => new() { Name = name, Address = address, Unit = unit, Min = min, Max = max };

    /// <summary>Definition with a nullable range, used to pin the 未配置范围 refusal at the VM.</summary>
    private static ParameterDefinition Def(string name, ushort address, string unit, int? min, int? max)
        => new() { Name = name, Address = address, Unit = unit, Min = min, Max = max };

    /// <summary>Read-only <see cref="ICommandGate"/> the tests control directly.</summary>
    private sealed class FakeGate : ICommandGate
    {
        public bool IsOnline { get; set; }
        public bool IsManualIdle { get; set; } = true;
    }

    /// <summary>
    /// Recording fake that emulates FC06/FC03 over a per-address register map. It records every register
    /// write and can be seeded to (a) override the read-back value, (b) throw on the read-back (comms
    /// interruption), or (c) hold writes on a gate so a write is observed in flight (the save-in-progress
    /// test). A held write records into the map only once <see cref="ReleaseHeldWrites"/> releases it.
    /// </summary>
    private sealed class FakeModbusClient : IModbusClient
    {
        private readonly Dictionary<ushort, ushort> _registers = new();
        private readonly List<TaskCompletionSource<bool>> _gates = new();

        public List<(ushort Address, ushort Value)> Writes { get; } = new();
        public ushort? OverrideReadBack { get; set; }
        public bool ThrowOnReadBack { get; set; }
        public bool HoldWrites { get; set; }

        public void ReleaseHeldWrites()
        {
            foreach (var gate in _gates)
            {
                if (!gate.Task.IsCompleted)
                {
                    gate.SetResult(true);
                }
            }

            _gates.Clear();
        }

        public Task WriteSingleRegisterAsync(byte slaveId, ushort address, ushort value, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (HoldWrites)
            {
                var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                _gates.Add(gate);
                return gate.Task.ContinueWith(
                    _ => RecordWrite(address, value), TaskScheduler.Default);
            }

            RecordWrite(address, value);
            return Task.CompletedTask;
        }

        public Task<ushort[]> ReadHoldingRegistersAsync(byte slaveId, ushort address, ushort count, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (ThrowOnReadBack)
            {
                throw new TimeoutException("simulated read-back timeout");
            }

            if (OverrideReadBack is ushort overrideValue)
            {
                return Task.FromResult(new[] { overrideValue });
            }

            return Task.FromResult(new[] { _registers.TryGetValue(address, out var value) ? value : (ushort)0 });
        }

        private void RecordWrite(ushort address, ushort value)
        {
            Writes.Add((address, value));
            _registers[address] = value;
        }

        public Task ConnectAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task DisconnectAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<bool[]> ReadCoilsAsync(byte slaveId, ushort address, ushort count, CancellationToken cancellationToken)
            => Task.FromResult(new bool[count]);

        public Task<bool[]> ReadDiscreteInputsAsync(byte slaveId, ushort address, ushort count, CancellationToken cancellationToken)
            => Task.FromResult(new bool[count]);

        public Task<ushort[]> ReadInputRegistersAsync(byte slaveId, ushort address, ushort count, CancellationToken cancellationToken)
            => Task.FromResult(new ushort[count]);

        public Task WriteSingleCoilAsync(byte slaveId, ushort address, bool value, CancellationToken cancellationToken)
            => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
