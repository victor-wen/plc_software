using PlcSoftware.App.ViewModels;
using PlcSoftware.Core.Models;
using PlcSoftware.Core.Services;

namespace PlcSoftware.App.Tests.ViewModels;

/// <summary>
/// Pins how <see cref="OverviewViewModel"/> maps a decoded <see cref="DeviceSnapshot"/> plus the
/// supervised link state into the read-only overview display (design §6.2): the 6-step flow highlight
/// driven by D200 + the single-hot M200-M205 flags, the key sensor / stopper state, the width / belt
/// speed / production readouts, and the offline behaviour (the displays grey out while the last-update
/// timestamp is preserved).
///
/// <para><b>No WPF dependency.</b> The view model only consumes Core snapshots and the
/// <see cref="ConnectionState"/> enum, so the suite runs under a pure unit-test host. It CANNOT run on
/// the WSL/Linux cross-build (WindowsDesktop runtime absent) — on Linux it only contributes a compile
/// RED/GREEN check; the full execution (GREEN) happens on the Windows CI runner.</para>
/// </summary>
public class OverviewViewModelTests
{
    private static IReadOnlyDictionary<string, object?> Snap(params (string Key, object? Value)[] values)
        => values.ToDictionary(v => v.Key, v => v.Value);

    /// <summary>Builds a consistent single-hot step snapshot: D200 = <paramref name="step"/> and exactly
    /// the M(200+step) flag true while the other five step flags are false.</summary>
    private static IReadOnlyDictionary<string, object?> StepSnap(int step)
    {
        var items = new List<(string Key, object? Value)> { ("D200", (ushort)step) };
        for (var i = 0; i < 6; i++)
        {
            items.Add(("M" + (200 + i), i == step));
        }

        return Snap(items.ToArray());
    }

    private static bool[] Highlights(OverviewViewModel vm)
        => new[] { vm.IsStep0, vm.IsStep1, vm.IsStep2, vm.IsStep3, vm.IsStep4, vm.IsStep5 };

    // --- D200 + M200-M205 step highlight mapping (design §6.2) ---

    [Fact]
    public void Snapshot_step_maps_active_step_and_highlight()
    {
        var vm = new OverviewViewModel();

        vm.ApplySnapshot(new DeviceSnapshot(StepSnap(2), DateTime.UtcNow));

        Assert.Equal(2, vm.StepNumber);
        Assert.Equal(2, vm.ActiveStep);
        Assert.Equal("挡停定位", vm.StepName);
        Assert.True(vm.IsStep2);
        Assert.False(vm.IsStep0);
        Assert.False(vm.IsStep1);
        Assert.False(vm.IsStep3);
        Assert.False(vm.IsStep4);
        Assert.False(vm.IsStep5);
    }

    [Theory]
    [InlineData(0, "等待进板")]
    [InlineData(1, "进料")]
    [InlineData(2, "挡停定位")]
    [InlineData(3, "触发相机")]
    [InlineData(4, "请求放行")]
    [InlineData(5, "放行")]
    public void Single_hot_step_snapshot_highlights_exactly_that_step(int step, string expectedName)
    {
        var vm = new OverviewViewModel();

        vm.ApplySnapshot(new DeviceSnapshot(StepSnap(step), DateTime.UtcNow));

        Assert.Equal(step, vm.StepNumber);
        Assert.Equal(step, vm.ActiveStep);
        Assert.Equal(expectedName, vm.StepName);

        // Single-hot sanity: exactly one step flag is true, and it is the D200/M(200+step) one.
        var highlights = Highlights(vm);
        for (var i = 0; i < highlights.Length; i++)
        {
            Assert.Equal(i == step, highlights[i]);
        }

        Assert.Equal(1, highlights.Count(h => h));
    }

    [Fact]
    public void Snapshot_without_step_flags_falls_back_to_step_number()
    {
        var vm = new OverviewViewModel();

        // D200 present but no M200-M205 flag: the step number alone still resolves the highlight.
        vm.ApplySnapshot(new DeviceSnapshot(Snap(("D200", (ushort)3)), DateTime.UtcNow));

        Assert.Equal(3, vm.StepNumber);
        Assert.Equal(3, vm.ActiveStep);
        Assert.Equal("触发相机", vm.StepName);
    }

    [Fact]
    public void Snapshot_with_multiple_step_flags_is_ambiguous_no_highlight()
    {
        var vm = new OverviewViewModel();

        // Two live step flags is a corrupt/racing snapshot: do not highlight a wrong step.
        vm.ApplySnapshot(new DeviceSnapshot(
            Snap(("D200", (ushort)1), ("M201", true), ("M205", true)),
            DateTime.UtcNow));

        Assert.Equal(1, vm.StepNumber);
        Assert.Null(vm.ActiveStep);
        Assert.Equal("未知", vm.StepName);
        Assert.All(Highlights(vm), h => Assert.False(h));
    }

    [Fact]
    public void Single_live_flag_wins_over_disagreeing_d200()
    {
        var vm = new OverviewViewModel();

        // D200 says step 1 but only M205 is live: the single live flag (fast group, polled fresher than
        // the process group) wins the highlight, while StepNumber still surfaces the raw D200 so the
        // divergence between the two is visible to the operator.
        vm.ApplySnapshot(new DeviceSnapshot(
            Snap(("D200", (ushort)1), ("M205", true)),
            DateTime.UtcNow));

        Assert.Equal(1, vm.StepNumber);
        Assert.Equal(5, vm.ActiveStep);
        Assert.Equal("放行", vm.StepName);
        Assert.True(vm.IsStep5);
        Assert.False(vm.IsStep1);
    }

    // --- Offline: last-update time preserved, read-only displays grey out ---

    [Fact]
    public void Online_state_keeps_displays_active_and_refreshes_last_update()
    {
        var vm = new OverviewViewModel();

        vm.ApplyConnectionState(ConnectionState.Online);

        Assert.Equal(ConnectionState.Online, vm.ConnectionState);
        Assert.True(vm.IsOnline);
        Assert.Equal("在线", vm.ConnectionStatusText);

        var timestamp = new DateTime(2026, 8, 27, 12, 0, 0, DateTimeKind.Utc);
        vm.ApplySnapshot(new DeviceSnapshot(StepSnap(0), timestamp));

        Assert.True(vm.IsOnline);
        Assert.Equal(timestamp, vm.LastUpdateTime);
    }

    [Fact]
    public void Disconnected_greys_out_displays_and_preserves_last_update()
    {
        var vm = new OverviewViewModel();
        var timestamp = new DateTime(2026, 8, 27, 12, 0, 0, DateTimeKind.Utc);
        vm.ApplySnapshot(new DeviceSnapshot(StepSnap(0), timestamp));
        Assert.True(vm.IsOnline);

        vm.ApplyConnectionState(ConnectionState.Disconnected);

        // All read-only displays grey out (IsOnline == false drives the XAML opacity).
        Assert.False(vm.IsOnline);
        Assert.Equal("离线", vm.ConnectionStatusText);
        // The frozen last snapshot is still shown with its timestamp.
        Assert.Equal(timestamp, vm.LastUpdateTime);
        Assert.Equal(timestamp.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"), vm.LastUpdateText);
    }

    [Fact]
    public void Offline_with_no_snapshot_reports_no_data()
    {
        var vm = new OverviewViewModel();

        vm.ApplyConnectionState(ConnectionState.Disconnected);

        Assert.False(vm.IsOnline);
        Assert.Null(vm.LastUpdateTime);
        Assert.Equal("无数据", vm.LastUpdateText);
    }

    [Fact]
    public void Snapshot_with_min_value_timestamp_reports_no_data()
    {
        var vm = new OverviewViewModel();

        // A seeded/empty store snapshot carries DateTime.MinValue (DateTime default). That is not a real
        // snapshot yet, so the overview must surface 无数据 instead of "0001-01-01 00:00:00".
        vm.ApplySnapshot(new DeviceSnapshot(new Dictionary<string, object?>(), DateTime.MinValue));

        Assert.Null(vm.LastUpdateTime);
        Assert.False(vm.HasData);
        Assert.Equal("无数据", vm.LastUpdateText);
    }

    // --- Key sensors, stopper, width, belt speed and production mapping (design §6.2) ---

    [Fact]
    public void Snapshot_maps_sensors_stopper_width_speed_and_production()
    {
        var vm = new OverviewViewModel();

        vm.ApplySnapshot(new DeviceSnapshot(Snap(
            ("D200", (ushort)4),
            ("M204", true),
            ("M313", true),   // 安全光栅 X7 — triggered/blocked.
            ("M314", true),   // 前门 X16 — open.
            ("M315", false),  // 后门 X17 — closed.
            ("M316", false),  // 气压 X22 — low.
            ("M303", true),   // 阻挡原位 X20 — at home.
            ("M304", false),  // 阻挡工作位 X21 — not extended.
            ("D202", (ushort)800),  // target width.
            ("D203", (ushort)795),  // current width.
            ("D205", (ushort)50),   // belt speed.
            (RegisterDecoder.ProductionCountKey, 123456u)), DateTime.UtcNow));

        Assert.True(vm.LightCurtain);
        Assert.Equal("遮挡", vm.LightCurtainStatus);
        Assert.True(vm.FrontDoor);
        Assert.Equal("打开", vm.FrontDoorStatus);
        Assert.False(vm.RearDoor);
        Assert.Equal("关闭", vm.RearDoorStatus);
        Assert.False(vm.AirPressure);
        Assert.Equal("低", vm.AirPressureStatus);

        Assert.True(vm.StopperHome);
        Assert.False(vm.StopperWork);
        Assert.Equal("原位", vm.StopperStatus);

        Assert.Equal(800, vm.TargetWidth);
        Assert.Equal(795, vm.CurrentWidth);
        Assert.Equal(50, vm.BeltSpeed);
        Assert.Equal(123456u, vm.ProductionCount);
    }
}
