using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PlcSoftware.Core.Abstractions;
using PlcSoftware.Core.Models;

namespace PlcSoftware.App.ViewModels;

/// <summary>
/// The manual page (design §6.4): 调宽正转 (M106) / 调宽反转 (M107) / 皮带点动 (M108) / 挡停 (M109) are
/// press-and-hold jogs. The commands only open when the machine is manual-idle (<see cref="ICommandGate.IsManualIdle"/>:
/// M1 manual + !M3 stopped), and every release event — mouse release / mouse-leave / focus loss / page switch /
/// window close (design §6.4) — is routed to <see cref="ReleaseAllJogsAsync"/> so no manual coil is left
/// latched, with the D106 watchdog of design §5.2 as the offline fallback.
///
/// <para><b>Structure (mirrors <see cref="OperationViewModel"/>).</b> The four press commands are
/// <c>AsyncRelayCommand</c>s gated by <see cref="CanJog"/> (which re-queries the injected
/// <see cref="ICommandGate"/>); pressing writes <c>true</c> to the targeted jog coil. Release is <em>not</em>
/// a command — the WPF <c>PressAndHoldBehavior</c> and the page/window wiring call the public
/// <see cref="ReleaseAllJogsAsync"/> (which routes to
/// <see cref="ICommandService.ReleaseJogCommandsAsync"/>, writing M106-M109 all false). The buttons only
/// need <see cref="IsJogEnabled"/> for the enabled/disabled presentation.</para>
///
/// <para><b>No UI-thread dependency.</b> The view model consumes Core snapshots + the supervised link state
/// through <see cref="ApplySnapshot"/> / <see cref="ApplyConnectionState"/> and executes writes through the
/// injected <see cref="ICommandService"/>, gated by the injected <see cref="ICommandGate"/>. It never touches
/// a <c>Dispatcher</c> or any WPF type, so it stays testable under a pure unit test host (the App tests are
/// CI-only on Windows because the WindowsDesktop runtime cannot run on the WSL cross-build, not because this
/// class needs WPF).</para>
/// </summary>
public sealed partial class ManualViewModel : ObservableObject
{
    private readonly ICommandService _commandService;
    private readonly ICommandGate _gate;

    /// <summary>The human-readable result of the last jog press (design §6.4 结果反馈).</summary>
    [ObservableProperty]
    private string _commandFeedbackText = string.Empty;

    /// <summary>The supervised link state, used only for the connection-status text (design §6.1).</summary>
    [ObservableProperty]
    private ConnectionState _connectionState;

    /// <summary>Builds the manual page over the injected command service and command gate.</summary>
    public ManualViewModel(ICommandService commandService, ICommandGate gate)
    {
        _commandService = commandService ?? throw new ArgumentNullException(nameof(commandService));
        _gate = gate ?? throw new ArgumentNullException(nameof(gate));
        _connectionState = ConnectionState.Disconnected;
    }

    /// <summary>True when the link is Online and host writes are permitted (design §5.3). Backed by the
    /// injected <see cref="ICommandGate"/> (AppCommandGate in production).</summary>
    public bool IsOnline => _gate.IsOnline;

    /// <summary>True when the machine is manual (M1) and stopped (!M3) — the only state the manual jogs open.
    /// Backed by the injected <see cref="ICommandGate"/>.</summary>
    public bool IsManualIdle => _gate.IsManualIdle;

    /// <summary>The jog buttons' enabled state (design §6.4: 只在 PLC 返回手动且非运行状态时开放).</summary>
    public bool IsJogEnabled => IsManualIdle;

    /// <summary>Human-readable link text (在线 / 离线 / …) for the manual-page header.</summary>
    public string ConnectionStatusText => ConnectionState switch
    {
        ConnectionState.Online => "在线",
        ConnectionState.Connecting => "连接中",
        ConnectionState.Reconnecting => "重连中",
        ConnectionState.HeartbeatLost => "心跳丢失",
        _ => "离线",
    };

    /// <summary>Human-readable open/closed hint for the manual jogs (design §6.4).</summary>
    public string JogAvailabilityText => IsManualIdle ? "可点动" : "不可点动";

    // --- Press commands (design §6.4): write the targeted jog coil true on press ----------------------

    [RelayCommand(CanExecute = nameof(CanJog))]
    private async Task WidthPlusJogAsync(CancellationToken cancellationToken)
        => await SendJogAsync(CommandTarget.ManualWidthPlus, "调宽+", cancellationToken);

    [RelayCommand(CanExecute = nameof(CanJog))]
    private async Task WidthMinusJogAsync(CancellationToken cancellationToken)
        => await SendJogAsync(CommandTarget.ManualWidthMinus, "调宽-", cancellationToken);

    [RelayCommand(CanExecute = nameof(CanJog))]
    private async Task BeltJogAsync(CancellationToken cancellationToken)
        => await SendJogAsync(CommandTarget.ManualBeltJog, "皮带点动", cancellationToken);

    [RelayCommand(CanExecute = nameof(CanJog))]
    private async Task StopperJogAsync(CancellationToken cancellationToken)
        => await SendJogAsync(CommandTarget.ManualStopper, "挡停", cancellationToken);

    /// <summary>The release-equivalent inverse for <b>press</b>: only a manual-idle machine opens the jogs
    /// (design §6.4). The PLC performs the final interlock.</summary>
    private bool CanJog() => _gate.IsManualIdle;

    // --- Release paths (design §6.4) ----------------------------------------------------------------

    /// <summary>
    /// Presses one jog coil (writes <c>true</c>). Called by <c>PressAndHoldBehavior</c> on mouse-down. The
    /// press is gated through the matching command's <c>CanExecute</c> (<see cref="CanJog"/>), so a jog that
    /// is not manual-idle is simply not started — matching a button disabled by <see cref="IsJogEnabled"/>.
    ///
    /// <para><b>Non-jog targets fail fast.</b> Only the four M106-M109 jog targets are valid here. Any other
    /// target (a mode/EStop/mask coil) means a jog button was mis-configured, which is a wiring bug — it is
    /// surfaced as an <see cref="ArgumentException"/> rather than silently ignored, so the mis-config is
    /// caught at the behavior boundary instead of leaving the operator with a dead button.</para>
    /// </summary>
    public void PressJog(CommandTarget target)
    {
        var command = target switch
        {
            CommandTarget.ManualWidthPlus => WidthPlusJogCommand,
            CommandTarget.ManualWidthMinus => WidthMinusJogCommand,
            CommandTarget.ManualBeltJog => BeltJogCommand,
            CommandTarget.ManualStopper => StopperJogCommand,
            _ => throw new ArgumentException(
                $"'{target}' is not a manual jog command target — a jog button may only target M106-M109.",
                nameof(target)),
        };

        if (command.CanExecute(null))
        {
            command.Execute(null);
        }
    }

    /// <summary>
    /// Releases <em>every</em> jog coil (writes M106-M109 all false via
    /// <see cref="ICommandService.ReleaseJogCommandsAsync"/>). This is the single safety hook wired to all
    /// four release triggers of design §6.4 — mouse release, mouse-leave, focus loss / window blur, and page
    /// switch / window close — so no manual coil is left latched when the operator releases or navigates away.
    /// Best-effort: the release writes do not observe an already-canceled app-exit token and each per-coil
    /// transport error is swallowed (see <c>CommandService.ReleaseJogCommandsAsync</c>); a thrown exception is
    /// likewise swallowed so a caller that fires-and-forgets cannot surface an unobserved fault.
    /// </summary>
    public async Task ReleaseAllJogsAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await _commandService.ReleaseJogCommandsAsync(cancellationToken);
        }
        catch
        {
            // Best-effort: the D106 watchdog (design §5.2) is the offline fallback for a latched coil.
        }
    }

    // --- State application (composition-root wired) --------------------------------------------------

    /// <summary>Applies an observed supervised-link state. Writes stay gated by <see cref="IsOnline"/>, but
    /// the link text is refreshed here and the jog CanExecute re-queries the gate.</summary>
    public void ApplyConnectionState(ConnectionState state)
    {
        ConnectionState = state;
        OnPropertyChanged(nameof(IsOnline));
        RequestJogRefresh();
    }

    /// <summary>Applies one decoded snapshot so the jog <c>CanExecute</c> re-queries the gate (the gate reads
    /// the live store, so <see cref="IsManualIdle"/> is already current at this point — this just refreshes the
    /// command/presentation state).</summary>
    public void ApplySnapshot(DeviceSnapshot snapshot)
    {
        if (snapshot is null)
        {
            throw new ArgumentNullException(nameof(snapshot));
        }

        RequestJogRefresh();
    }

    // --- Command execution helpers -------------------------------------------------------------------

    private async Task SendJogAsync(CommandTarget target, string label, CancellationToken cancellationToken)
    {
        try
        {
            var result = await _commandService.ExecuteAsync(new CommandRequest(target, true), cancellationToken);
            CommandFeedbackText = FormatFeedback(label, result);
        }
        catch (Exception ex)
        {
            // A transport/command failure must never escape to the AsyncRelayCommand (it would surface on the
            // UI thread): report it on the feedback line instead and keep the UI alive.
            CommandFeedbackText = $"命令失败：{ex.Message}";
        }
    }

    private static string FormatFeedback(string label, CommandResult result)
        => result.Status switch
        {
            CommandStatus.Success => $"{label}已按下",
            CommandStatus.Rejected => string.IsNullOrEmpty(result.Message)
                ? $"{label}被拒绝"
                : $"{label}被拒绝：{result.Message}",
            CommandStatus.Unknown => string.IsNullOrEmpty(result.Message)
                ? $"{label}状态未知"
                : $"{label}状态未知：{result.Message}",
            _ => string.Empty,
        };

    private void RequestJogRefresh()
    {
        OnPropertyChanged(nameof(IsManualIdle));
        OnPropertyChanged(nameof(IsJogEnabled));
        OnPropertyChanged(nameof(JogAvailabilityText));
        WidthPlusJogCommand.NotifyCanExecuteChanged();
        WidthMinusJogCommand.NotifyCanExecuteChanged();
        BeltJogCommand.NotifyCanExecuteChanged();
        StopperJogCommand.NotifyCanExecuteChanged();
    }

    partial void OnConnectionStateChanged(ConnectionState value) => OnPropertyChanged(nameof(ConnectionStatusText));
}
