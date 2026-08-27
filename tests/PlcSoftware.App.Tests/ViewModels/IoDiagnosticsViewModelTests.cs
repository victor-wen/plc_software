using PlcSoftware.App.ViewModels;
using PlcSoftware.Core.Models;

namespace PlcSoftware.App.Tests.ViewModels;

/// <summary>
/// Pins how <see cref="IoDiagnosticsViewModel"/> presents the read-only I/O table (design §6.6): the X/Y/M
/// points from the injected point map are grouped and displayed read-only, with the physical state resolved
/// from the decoded snapshot where a mirror exists. The X inputs are matched through the M300+ echo registers
/// (design §4.6, e.g. X20 → M303 阻挡原位), the M relays are read straight from their own snapshot key, and a
/// Y output (or an X input with no M echo, e.g. X0-X3) shows 未上报 (unavailable offline).
///
/// <para><b>No force-write entry (design §6.6 + Gate 7).</b> The table is presentation-only: it exposes no
/// write command and no per-point writable flag, so manual actions can only be driven from the manual page
/// (design §6.4). The injected point map's <c>IsWritable</c> flags are deliberately ignored.</para>
///
/// <para><b>No WPF dependency.</b> The view model consumes <see cref="ConnectionState"/> and
/// <see cref="DeviceSnapshot"/> through <see cref="IoDiagnosticsViewModel.ApplyConnectionState"/> /
/// <see cref="IoDiagnosticsViewModel.ApplySnapshot"/> and never touches any WPF type, so it stays a pure unit
/// test host (the App tests are CI-only on Windows; on Linux it only contributes a compile RED/GREEN check).</para>
/// </summary>
public class IoDiagnosticsViewModelTests
{
    [Fact]
    public void Point_map_is_grouped_into_x_y_m()
    {
        // A writable M (M100) and D-register entries are included to pin that the I/O table ignores the
        // point map's writable flags and only surfaces the X/Y/M relays for read-only presentation.
        var vm = Build(
            P("X4", "进板感应", 4),
            P("X20", "阻挡原位", 16),
            P("Y6", "挡停气缸", 6),
            P("M0", "急停有效", 0),
            P("M100", "上位机急停请求", 100, writable: true),
            P("M303", "阻挡原位X20", 303),
            P("D100", "M0-M15映射", 0));

        Assert.All(vm.Inputs, r => Assert.Equal("X", r.Group));
        Assert.All(vm.Outputs, r => Assert.Equal("Y", r.Group));
        Assert.All(vm.Relays, r => Assert.Equal("M", r.Group));

        Assert.Contains(vm.Inputs, r => r.Address == "X4");
        Assert.Contains(vm.Inputs, r => r.Address == "X20");
        Assert.Contains(vm.Outputs, r => r.Address == "Y6");
        Assert.Contains(vm.Relays, r => r.Address == "M0");
        Assert.Contains(vm.Relays, r => r.Address == "M303");

        // D registers (the packed M0-M15 map itself) are not an X/Y/M relay and are excluded.
        Assert.DoesNotContain(vm.Inputs.Concat(vm.Outputs).Concat(vm.Relays), r => r.Address == "D100");
        // A point that appears in one group cannot leak into another.
        Assert.DoesNotContain(vm.Inputs, r => r.Address == "Y6");
    }

    [Fact]
    public void X_values_are_matched_from_the_m300_echo_register()
    {
        var vm = Build(P("X4", "进板感应", 4), P("X20", "阻挡原位", 16), P("X0", "急停按钮", 0));

        vm.ApplySnapshot(Snap(("M300", true), ("M303", true), ("M301", false)));

        // X4 → M300 (接通), X20 → M303 (接通).
        var x4 = vm.Inputs.Single(r => r.Address == "X4");
        Assert.Equal(true, x4.State);
        Assert.Equal("接通", x4.StateText);
        Assert.True(x4.HasValue);
        Assert.Equal(true, vm.Inputs.Single(r => r.Address == "X20").State);

        // X0 (急停) has no M echo (X0-X3 are not mirrored onto M300+), so it is reported as 未上报.
        var x0 = vm.Inputs.Single(r => r.Address == "X0");
        Assert.Null(x0.State);
        Assert.Equal("未上报", x0.StateText);
        Assert.False(x0.HasValue);
    }

    [Fact]
    public void M_values_are_read_directly_from_the_snapshot()
    {
        var vm = Build(P("M0", "急停有效", 0), P("M200", "步骤0等待进板", 200), P("M316", "气压检测X22", 316));

        vm.ApplySnapshot(Snap(("M0", true), ("M200", false), ("M316", true)));

        Assert.Equal(true, vm.Relays.Single(r => r.Address == "M0").State);
        Assert.Equal("接通", vm.Relays.Single(r => r.Address == "M0").StateText);
        Assert.Equal(false, vm.Relays.Single(r => r.Address == "M200").State);
        Assert.Equal("断开", vm.Relays.Single(r => r.Address == "M200").StateText);
        Assert.Equal(true, vm.Relays.Single(r => r.Address == "M316").State);
    }

    [Fact]
    public void Y_outputs_have_no_snapshot_echo_and_show_unreported()
    {
        var vm = Build(P("Y6", "挡停气缸", 6));

        // Even a stray Y key in the snapshot cannot resolve, because a Y output has no echo key.
        vm.ApplySnapshot(Snap(("Y6", true)));

        Assert.Null(vm.Outputs.Single(r => r.Address == "Y6").State);
        Assert.Equal("未上报", vm.Outputs.Single(r => r.Address == "Y6").StateText);
    }

    [Fact]
    public void Missing_snapshot_key_leaves_the_row_unreported()
    {
        var vm = Build(P("M303", "阻挡原位X20", 303));

        vm.ApplySnapshot(Snap(("M304", true))); // M303 is absent from this snapshot.

        Assert.Null(vm.Relays.Single(r => r.Address == "M303").State);
        Assert.Equal("未上报", vm.Relays.Single(r => r.Address == "M303").StateText);
    }

    [Fact]
    public void Connection_state_drives_the_header()
    {
        var vm = Build(P("X0", "急停按钮", 0));

        vm.ApplyConnectionState(ConnectionState.Online);
        Assert.True(vm.IsOnline);
        Assert.Equal("在线", vm.ConnectionStatusText);

        vm.ApplyConnectionState(ConnectionState.Disconnected);
        Assert.False(vm.IsOnline);
        Assert.Equal("离线", vm.ConnectionStatusText);
    }

    /// <summary>Read-only presentation (design §6.6 + Gate 7): the I/O table exposes no force-write command
    /// and no per-point writable flag — manual actions can only be driven from the manual page.</summary>
    [Fact]
    public void No_force_write_command_or_writable_flag_is_exposed()
    {
        // A writable point (M100) must still be presented read-only; its IsWritable flag is ignored.
        var vm = Build(P("X0", "急停按钮", 0), P("M100", "上位机急停请求", 100, writable: true), P("Y6", "挡停气缸", 6));

        // No command surface: the VM has no ICommand (or IRelayCommand) property a force-write could ride on.
        var commands = vm.GetType()
            .GetProperties()
            .Where(p => typeof(System.Windows.Input.ICommand).IsAssignableFrom(p.PropertyType))
            .Select(p => p.Name)
            .ToArray();
        Assert.Empty(commands);

        // No per-row writable toggle either.
        Assert.DoesNotContain(typeof(IoRow).GetProperties(), p => p.Name is "IsWritable" or "IsEditable");
    }

    private static DeviceSnapshot Snap(params (string Key, object? Value)[] values)
        => new(values.ToDictionary(v => v.Key, v => v.Value), DateTime.UtcNow);

    private static IoDiagnosticsViewModel Build(params PointDefinition[] points) => new(points);

    private static PointDefinition P(string address, string name, ushort protocol, bool writable = false)
        => new() { Address = address, Name = name, ProtocolAddress = protocol, IsWritable = writable };
}
