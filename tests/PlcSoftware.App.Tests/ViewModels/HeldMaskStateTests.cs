using PlcSoftware.App.Services;
using PlcSoftware.Core.Abstractions;
using PlcSoftware.Core.Models;

namespace PlcSoftware.App.Tests.ViewModels;

/// <summary>
/// Pins the App-layer held-mask state path (review finding: mask status data path dead). M110/M111 are
/// holding <em>commands</em>, not PLC feedback points — the fast-block register map has no slot for them and
/// we must not invent one — so the 屏蔽 flags are driven by the HMI's own last successfully-held command
/// value (design §4.4: 保持写入，持续确认并审计), tracked by <see cref="SimpleHeldStateService"/> and recorded by
/// the <see cref="CommandServiceMaskAware"/> decorator.
///
/// <para>These tests are deliberate, WPF-free unit tests (compile-only on the WSL/Linux cross-build; they
/// run on the Windows CI where the WindowsDesktop runtime exists).</para>
/// </summary>
public class HeldMaskStateTests
{
    // --- SimpleHeldStateService: holds the commanded value and raises only on change -------------

    [Fact]
    public void Record_successful_bypass_holds_true()
    {
        var held = new SimpleHeldStateService();

        held.Record(CommandTarget.LightCurtainBypass, true);
        Assert.True(held.LightCurtainBypass);

        held.Record(CommandTarget.DoorBypass, true);
        Assert.True(held.DoorBypass);
    }

    [Fact]
    public void Record_release_holds_false()
    {
        var held = new SimpleHeldStateService();
        held.Record(CommandTarget.LightCurtainBypass, true);

        held.Record(CommandTarget.LightCurtainBypass, false);

        Assert.False(held.LightCurtainBypass);
    }

    [Fact]
    public void Record_non_mask_target_is_ignored()
    {
        var held = new SimpleHeldStateService();

        held.Record(CommandTarget.AutoMode, true);
        held.Record(CommandTarget.DoorBypass, false);
        held.Record(CommandTarget.AutoMode, false);

        Assert.False(held.LightCurtainBypass);
        Assert.False(held.DoorBypass);
    }

    [Fact]
    public void MaskStateChanged_raises_only_when_a_flag_flips()
    {
        var held = new SimpleHeldStateService();
        var events = 0;
        held.MaskStateChanged += () => events++;

        held.Record(CommandTarget.LightCurtainBypass, true);
        Assert.Equal(1, events);

        // Redundant write to the same target does not re-raise.
        held.Record(CommandTarget.LightCurtainBypass, true);
        Assert.Equal(1, events);

        held.Record(CommandTarget.DoorBypass, true);
        Assert.Equal(2, events);
    }

    // --- CommandServiceMaskAware: records the write outcome into the held-state tracker -------------

    [Fact]
    public async Task ExecuteAsync_successful_mask_command_holds_commanded_value()
    {
        var inner = new FakeCommandService(_ => new CommandResult(CommandStatus.Success, CommandTarget.LightCurtainBypass));
        var held = new SimpleHeldStateService();
        var service = new CommandServiceMaskAware(inner, held);

        await service.ExecuteAsync(new CommandRequest(CommandTarget.LightCurtainBypass, true), CancellationToken.None);

        Assert.True(held.LightCurtainBypass);
    }

    [Fact]
    public async Task ExecuteAsync_release_mask_command_holds_false()
    {
        var inner = new FakeCommandService(_ => new CommandResult(CommandStatus.Success, CommandTarget.DoorBypass));
        var held = new SimpleHeldStateService();
        var service = new CommandServiceMaskAware(inner, held);

        await service.ExecuteAsync(new CommandRequest(CommandTarget.DoorBypass, false), CancellationToken.None);

        Assert.False(held.DoorBypass);
    }

    [Fact]
    public async Task ExecuteAsync_failed_mask_command_holds_false()
    {
        // A Rejected (offline gate) or Unknown (transport timeout) outcome cannot confirm the hold, so the
        // tracker conservatively reports not-bypassed.
        var inner = new FakeCommandService(_ => new CommandResult(CommandStatus.Unknown, CommandTarget.LightCurtainBypass, "timeout"));
        var held = new SimpleHeldStateService();
        held.Record(CommandTarget.LightCurtainBypass, true);
        var service = new CommandServiceMaskAware(inner, held);

        await service.ExecuteAsync(new CommandRequest(CommandTarget.LightCurtainBypass, true), CancellationToken.None);

        Assert.False(held.LightCurtainBypass);
    }

    [Fact]
    public async Task ExecuteAsync_non_mask_command_does_not_touch_held_state()
    {
        var inner = new FakeCommandService(_ => new CommandResult(CommandStatus.Success, CommandTarget.AutoMode));
        var held = new SimpleHeldStateService();
        var service = new CommandServiceMaskAware(inner, held);

        await service.ExecuteAsync(new CommandRequest(CommandTarget.AutoMode, true), CancellationToken.None);

        Assert.False(held.LightCurtainBypass);
        Assert.False(held.DoorBypass);
    }

    [Fact]
    public async Task ExecuteAsync_propagates_inner_result_unchanged()
    {
        var expected = new CommandResult(CommandStatus.Rejected, CommandTarget.DoorBypass, "link offline");
        var inner = new FakeCommandService(_ => expected);
        var service = new CommandServiceMaskAware(inner, new SimpleHeldStateService());

        var result = await service.ExecuteAsync(new CommandRequest(CommandTarget.DoorBypass, true), CancellationToken.None);

        Assert.Same(expected, result);
    }

    /// <summary>Stands in for the real <see cref="CommandService"/>; returns a configurable result.</summary>
    private sealed class FakeCommandService : ICommandService
    {
        private readonly Func<CommandRequest, CommandResult> _handler;

        public FakeCommandService(Func<CommandRequest, CommandResult> handler)
        {
            _handler = handler;
        }

        public Task<CommandResult> ExecuteAsync(CommandRequest request, CancellationToken cancellationToken)
            => Task.FromResult(_handler(request));

        public Task ReleaseJogCommandsAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
