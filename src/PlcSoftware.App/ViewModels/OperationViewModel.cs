using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PlcSoftware.Core.Abstractions;
using PlcSoftware.Core.Models;

namespace PlcSoftware.App.ViewModels;

/// <summary>
/// The operation zone (design §6.3): 启动 (M101) / 停止 (M102) / 复位 (M103) / 急停请求 (M100) and the
/// 自动 (M104) / 手动 (M104=0,M105=0) / 直通 (M105) mode switches. Commands execute through the injected
/// <see cref="ICommandService"/> (composition-root wired, the same decorator chain used by every other
/// command path) and are gated by the injected <see cref="ICommandGate"/> (the app's read-only link +
/// machine state, so the view model never touches the connection supervisor or the state store directly).
///
/// <para><b>No UI-thread dependency.</b> The view model consumes Core snapshots and the supervised link
/// state through <see cref="ApplySnapshot"/> / <see cref="ApplyConnectionState"/>. It never touches a
/// <c>Dispatcher</c> or any WPF type, so it stays testable under a pure unit test host (the App tests are
/// CI-only on Windows because the WindowsDesktop runtime cannot run on the WSL cross-build, not because
/// this class needs WPF).</para>
///
/// <para><b>CanExecute (design §6.3).</b> Button availability is a best-effort pre-gate that combines the
/// connection state, the PLC-reported mode, the run state and the fault state — the PLC performs the final
/// interlock. All commands are disabled while offline (design §5.3 forbids every write). The documented
/// per-command rules are:
/// <list type="bullet">
/// <item>启动 (M101): Manual + stopped + no fault — a manual-initiated run.</item>
/// <item>停止 (M102): online only — the operator must always be able to stop.</item>
/// <item>复位 (M103): stopped (the machine must not run while a fault is cleared); the fault itself does not block it.</item>
/// <item>急停请求 (M100): online only — a software stop request.</item>
/// <item>自动 (M104) / 直通 (M105): stopped + no fault — entering a non-manual mode is safety-gated.</item>
/// <item>手动 (M104=0,M105=0): stopped (not fault-gated, so the operator can recover).</item>
/// </list></para>
///
/// <para><b>Mode confirmation (design §4.4).</b> A mode switch writes the mutually-exclusive M104/M105
/// combo (手动 M104=0,M105=0 / 自动 M104=1,M105=0 / 直通 M104=0,M105=1) and then waits for the PLC to report
/// the final mode back on M1/M2/M13. The UI never trusts a single write result: the writes only set
/// <see cref="PendingMode"/>; the displayed <see cref="Mode"/> and the "switched" state change only once a
/// snapshot confirms M1/M2/M13. A confirmed mode that matches the request clears the pending flag (switch
/// took); a confirmed mode that differs also clears it (the PLC refused — nothing is in flight). An
/// <see cref="MachineMode.Unknown"/> snapshot (no mode bit) keeps the pending flag so a switch still in
/// flight is not cleared by a transient partial read.</para>
///
/// <para><b>软件急停请求.</b> <see cref="EStopRequestText"/> / <see cref="EStopRequestHint"/> make it
/// explicit that M100 is a <em>software</em> request only — never a physical e-stop button — so the
/// operator is not misled into depending on it for hard safety (design §4.4: 仅为软件停机请求).</para>
/// </summary>
public sealed partial class OperationViewModel : ObservableObject
{
    private readonly ICommandService _commandService;
    private readonly ICommandGate _gate;

    /// <summary>The PLC-confirmed machine mode, echoed on M1/M2/M13 (design §4.4).</summary>
    [ObservableProperty]
    private MachineMode _mode;

    /// <summary>The PLC run bit (M3).</summary>
    [ObservableProperty]
    private bool _isRunning;

    /// <summary>True when D110 reports a non-zero fault code (design §6.7).</summary>
    [ObservableProperty]
    private bool _hasFault;

    /// <summary>A mode switch requested by the operator but not yet confirmed by a snapshot.</summary>
    [ObservableProperty]
    private MachineMode? _pendingMode;

    /// <summary>The supervised link state, used only for the connection-status text (design §6.1).</summary>
    [ObservableProperty]
    private ConnectionState _connectionState;

    /// <summary>The human-readable result of the last command (design §6.3 结果反馈).</summary>
    [ObservableProperty]
    private string _commandFeedbackText = string.Empty;

    /// <summary>Builds the operation zone over the injected command service and command gate.</summary>
    public OperationViewModel(ICommandService commandService, ICommandGate gate)
    {
        _commandService = commandService ?? throw new ArgumentNullException(nameof(commandService));
        _gate = gate ?? throw new ArgumentNullException(nameof(gate));
        _connectionState = ConnectionState.Disconnected;
        _mode = MachineMode.Unknown;
    }

    /// <summary>True when the link is Online and host writes are permitted (design §5.3). Backed by the
    /// injected <see cref="ICommandGate"/> (AppCommandGate in production: supervisor Online).</summary>
    public bool IsOnline => _gate.IsOnline;

    /// <summary>True while a mode switch request is awaiting PLC confirmation.</summary>
    public bool IsModeSwitchPending => PendingMode is not null;

    /// <summary>Human-readable link text (在线 / 离线 / …) for the operation-zone header.</summary>
    public string ConnectionStatusText => ConnectionState switch
    {
        ConnectionState.Online => "在线",
        ConnectionState.Connecting => "连接中",
        ConnectionState.Reconnecting => "重连中",
        ConnectionState.HeartbeatLost => "心跳丢失",
        _ => "离线",
    };

    /// <summary>Human-readable PLC-confirmed mode (手动 / 自动 / 直通 / 未知).</summary>
    public string ModeText => Mode switch
    {
        MachineMode.Manual => "手动",
        MachineMode.Auto => "自动",
        MachineMode.Bypass => "直通",
        _ => "未知",
    };

    /// <summary>Marks the e-stop request as a software request (design §4.4: 仅为软件停机请求).</summary>
    public string EStopRequestText => "软件急停请求";

    /// <summary>The clarifying hint that this is a software request, not a physical e-stop.</summary>
    public string EStopRequestHint => "仅软件停机请求，非物理急停按钮";

    // --- Commands (design §6.3 操作区) ------------------------------------------------------------

    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task StartAsync(CancellationToken cancellationToken)
        => await SendPulseAsync(CommandTarget.Start, "启动", cancellationToken);

    [RelayCommand(CanExecute = nameof(CanStop))]
    private async Task StopAsync(CancellationToken cancellationToken)
        => await SendPulseAsync(CommandTarget.Stop, "停止", cancellationToken);

    [RelayCommand(CanExecute = nameof(CanReset))]
    private async Task ResetAsync(CancellationToken cancellationToken)
        => await SendPulseAsync(CommandTarget.Reset, "复位", cancellationToken);

    [RelayCommand(CanExecute = nameof(CanEStopRequest))]
    private async Task EStopRequestAsync(CancellationToken cancellationToken)
        => await SendPulseAsync(CommandTarget.EStopRequest, "急停", cancellationToken);

    [RelayCommand(CanExecute = nameof(CanAutoMode))]
    private async Task AutoModeAsync(CancellationToken cancellationToken)
        => await SendModeAsync(CommandTarget.AutoMode, primaryValue: true, CommandTarget.BypassMode, secondaryValue: false,
            label: "自动", requested: MachineMode.Auto, cancellationToken);

    [RelayCommand(CanExecute = nameof(CanBypassMode))]
    private async Task BypassModeAsync(CancellationToken cancellationToken)
        => await SendModeAsync(CommandTarget.AutoMode, primaryValue: false, CommandTarget.BypassMode, secondaryValue: true,
            label: "直通", requested: MachineMode.Bypass, cancellationToken);

    [RelayCommand(CanExecute = nameof(CanManualMode))]
    private async Task ManualModeAsync(CancellationToken cancellationToken)
        => await SendModeAsync(CommandTarget.AutoMode, primaryValue: false, CommandTarget.BypassMode, secondaryValue: false,
            label: "手动", requested: MachineMode.Manual, cancellationToken);

    // --- CanExecute predicates (see class docs for the rationale of each rule) ---------------------

    private bool CanStart() => IsOnline && Mode == MachineMode.Manual && !IsRunning && !HasFault;

    private bool CanStop() => IsOnline;

    private bool CanReset() => IsOnline && !IsRunning;

    private bool CanEStopRequest() => IsOnline;

    private bool CanAutoMode() => IsOnline && !IsRunning && !HasFault;

    private bool CanBypassMode() => IsOnline && !IsRunning && !HasFault;

    private bool CanManualMode() => IsOnline && !IsRunning;

    // --- State application (composition-root wired) -----------------------------------------------

    /// <summary>Applies an observed supervised-link state. Writes stay gated by <see cref="IsOnline"/>
    /// (the injected gate), but the link text is refreshed here so the header always reflects the latest
    /// state and the command CanExecute re-queries the gate.</summary>
    public void ApplyConnectionState(ConnectionState state)
    {
        ConnectionState = state;
        OnPropertyChanged(nameof(IsOnline));
        RaiseCanExecuteChanged();
    }

    /// <summary>
    /// Applies one decoded snapshot: the PLC-confirmed mode (M1/M2/M13), the run bit (M3) and the fault
    /// code (D110), then reconciles <see cref="PendingMode"/> against the confirmed mode.
    /// </summary>
    public void ApplySnapshot(DeviceSnapshot snapshot)
    {
        if (snapshot is null)
        {
            throw new ArgumentNullException(nameof(snapshot));
        }

        var values = snapshot.Values;

        // Mode precedence: auto (M2) > bypass (M13) > manual (M1) > unknown — the PLC's confirmed mode.
        if (ReadBool(values, "M2"))
        {
            Mode = MachineMode.Auto;
        }
        else if (ReadBool(values, "M13"))
        {
            Mode = MachineMode.Bypass;
        }
        else if (ReadBool(values, "M1"))
        {
            Mode = MachineMode.Manual;
        }
        else
        {
            Mode = MachineMode.Unknown;
        }

        IsRunning = ReadBool(values, "M3");
        HasFault = (ReadInt(values, "D110") ?? 0) != 0;

        // Resolve a pending mode switch against the PLC's confirmed mode (design §4.4). A concrete mode
        // means the request either took (Mode == PendingMode) or was refused (Mode != PendingMode) — either
        // way nothing is in flight. An Unknown snapshot keeps pending so an in-flight switch is not dropped
        // by a transient partial read.
        if (PendingMode is not null && Mode != MachineMode.Unknown)
        {
            PendingMode = null;
        }
    }

    // --- Command execution helpers ---------------------------------------------------------------

    private async Task SendPulseAsync(CommandTarget target, string label, CancellationToken cancellationToken)
    {
        var result = await _commandService.ExecuteAsync(new CommandRequest(target), cancellationToken);
        CommandFeedbackText = FormatFeedback(label, result);
    }

    /// <summary>Composes the mutually-exclusive M104/M105 mode write (design §4.4) and pends the requested
    /// mode only when both writes succeed; a non-success outcome (offline/unknown) reports feedback and
    /// leaves no pending mode, because the switch cannot be confirmed.</summary>
    private async Task SendModeAsync(
        CommandTarget primary, bool primaryValue,
        CommandTarget secondary, bool secondaryValue,
        string label, MachineMode requested,
        CancellationToken cancellationToken)
    {
        var first = await _commandService.ExecuteAsync(new CommandRequest(primary, primaryValue), cancellationToken);
        var second = await _commandService.ExecuteAsync(new CommandRequest(secondary, secondaryValue), cancellationToken);

        if (first.Status == CommandStatus.Success && second.Status == CommandStatus.Success)
        {
            PendingMode = requested;
            CommandFeedbackText = $"{label}模式已请求，等待PLC确认";
        }
        else
        {
            // Reflect the failing write (if any); the PLC state remains the source of truth.
            var worst = first.Status == CommandStatus.Success ? second : first;
            CommandFeedbackText = FormatFeedback(label, worst);
        }
    }

    private static string FormatFeedback(string label, CommandResult result)
        => result.Status switch
        {
            CommandStatus.Success => $"{label}命令已下发",
            CommandStatus.Rejected => string.IsNullOrEmpty(result.Message)
                ? $"{label}命令被拒绝"
                : $"{label}命令被拒绝：{result.Message}",
            CommandStatus.Unknown => string.IsNullOrEmpty(result.Message)
                ? $"{label}命令状态未知"
                : $"{label}命令状态未知：{result.Message}",
            _ => string.Empty,
        };

    private void RaiseCanExecuteChanged()
    {
        StartCommand.NotifyCanExecuteChanged();
        StopCommand.NotifyCanExecuteChanged();
        ResetCommand.NotifyCanExecuteChanged();
        EStopRequestCommand.NotifyCanExecuteChanged();
        AutoModeCommand.NotifyCanExecuteChanged();
        BypassModeCommand.NotifyCanExecuteChanged();
        ManualModeCommand.NotifyCanExecuteChanged();
    }

    partial void OnModeChanged(MachineMode value)
    {
        OnPropertyChanged(nameof(ModeText));
        RaiseCanExecuteChanged();
    }

    partial void OnIsRunningChanged(bool value) => RaiseCanExecuteChanged();

    partial void OnHasFaultChanged(bool value) => RaiseCanExecuteChanged();

    partial void OnPendingModeChanged(MachineMode? value) => OnPropertyChanged(nameof(IsModeSwitchPending));

    partial void OnConnectionStateChanged(ConnectionState value) => OnPropertyChanged(nameof(ConnectionStatusText));

    private static bool ReadBool(IReadOnlyDictionary<string, object?> values, string key)
        => values.TryGetValue(key, out var value) && value switch
        {
            bool b => b,
            ushort u => u != 0,
            int i => i != 0,
            uint ui => ui != 0,
            _ => false,
        };

    private static int? ReadInt(IReadOnlyDictionary<string, object?> values, string key)
        => values.TryGetValue(key, out var value)
            ? value switch
            {
                ushort u => u,
                int i => i,
                uint ui => (int)ui,
                short s => s,
                byte b => b,
                _ => null,
            }
            : null;
}
