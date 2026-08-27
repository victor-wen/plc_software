using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PlcSoftware.Core.Abstractions;
using PlcSoftware.Core.Models;
using PlcSoftware.Core.Services;

namespace PlcSoftware.App.ViewModels;

/// <summary>
/// One selectable read function offered by the debug terminal. The choice carries a Chinese label
/// (for the ComboBox) and the <see cref="TerminalReadFunction"/> the view model dispatches to.
/// </summary>
public sealed class TerminalReadChoice
{
    public TerminalReadChoice(string label, TerminalReadFunction function)
    {
        Label = label ?? throw new ArgumentNullException(nameof(label));
        Function = function;
    }

    /// <summary>Human-readable label, e.g. 读取保持寄存器 (FC03).</summary>
    public string Label { get; }

    /// <summary>The <see cref="DiagnosticTerminalService"/> read method this choice routes to.</summary>
    public TerminalReadFunction Function { get; }
}

/// <summary>
/// The Modbus debug-terminal read function codes: FC01 coils, FC02 discrete inputs, FC03 holding
/// registers, FC04 input registers. Writes (FC05 coil / FC06 register) are separate commands, not
/// part of the read selector.
/// </summary>
public enum TerminalReadFunction
{
    Coils,
    Discrete,
    Holding,
    Input,
}

/// <summary>
/// The structured Modbus debug terminal page (design §6.5): FC01/02/03/04 reads and FC05/06 single-point
/// writes, exposed as a safe, read-biased surface over the injected <see cref="DiagnosticTerminalService"/>.
///
/// <para><b>Reads (always permitted).</b> The operator picks a read function (读取线圈 / 读取离散 / 读取保持 /
/// 读取输入), enters the slave id / address / count and runs it; the hex rendering and elapsed time are shown
/// (结果反馈: HexResult + ElapsedMs), and any transport/bounds failure lands on <see cref="StatusText"/> /
/// <see cref="ErrorText"/> — nothing is ever thrown to the UI thread.</para>
///
/// <para><b>Writes (locked + stop-gated).</b> FC05/FC06 writes are permitted only while the terminal is
/// unlocked (<see cref="IsUnlocked"/>) and, downstream, while the machine is not running (the service's
/// injected <c>isRunningProvider</c>). <see cref="UnlockCommand"/> grants the 5-minute write unlock (the
/// service auto-locks; a write refused by the lock or the running guard surfaces on <see cref="StatusText"/>).
/// The write commands additionally require the link to be <see cref="IsOnline"/> (design §5.3 断线禁止写入).</para>
///
/// <para><b>No UI-thread dependency.</b> The view model consumes Core state through
/// <see cref="ApplyConnectionState"/> and executes everything through the injected
/// <see cref="DiagnosticTerminalService"/> (which itself sits behind the shared single-flight client queue,
/// so the terminal cannot bypass the request queue). It never touches a <c>Dispatcher</c> or any WPF type,
/// so it stays testable under a pure unit test host (the App tests are CI-only on Windows because the
/// WindowsDesktop runtime cannot run on the WSL cross-build, not because this class needs WPF).</para>
/// </summary>
public sealed partial class DiagnosticTerminalViewModel : ObservableObject
{
    private readonly DiagnosticTerminalService _service;
    private readonly ICommandGate _gate;
    private readonly IReadOnlyList<TerminalReadChoice> _readFunctions;

    /// <summary>The supervised link state, used only for the connection-status text (design §6.1).</summary>
    [ObservableProperty]
    private ConnectionState _connectionState;

    /// <summary>True while a debug-terminal command is in flight (guards a rapid double-click).</summary>
    [ObservableProperty]
    private bool _isBusy;

    /// <summary>The 1-based slave id the operator typed.</summary>
    [ObservableProperty]
    private string _slaveId = "1";

    /// <summary>The 0-based start address the operator typed.</summary>
    [ObservableProperty]
    private string _address = "0";

    /// <summary>The item count for a read (coils/discrete inputs/registers) the operator typed.</summary>
    [ObservableProperty]
    private string _count = "1";

    /// <summary>The value being written (FC05 bool / FC06 ushort) the operator typed.</summary>
    [ObservableProperty]
    private string _value = "0";

    /// <summary>The selected read function (default 读取保持寄存器 FC03).</summary>
    [ObservableProperty]
    private TerminalReadChoice? _selectedReadFunction;

    /// <summary>True while the write-unlock is granted (mirrors the service's <see cref="DiagnosticTerminalService.IsUnlocked"/>).</summary>
    [ObservableProperty]
    private bool _isUnlocked;

    /// <summary>The last successful operation's hex/ASCII rendering (design §6.5 十六进制数据显示), or empty.</summary>
    [ObservableProperty]
    private string _hexResult = string.Empty;

    /// <summary>The last operation's internal elapsed time in ms (design §6.5 响应耗时), or empty.</summary>
    [ObservableProperty]
    private string _elapsedMs = string.Empty;

    /// <summary>The reason the last operation failed (unlock / running / bounds / transport), or null on success.</summary>
    [ObservableProperty]
    private string? _errorText;

    /// <summary>The human-readable outcome of the last operation (成功 / 失败 / …).</summary>
    [ObservableProperty]
    private string _statusText = string.Empty;

    /// <summary>Builds the debug terminal over the injected terminal service and command gate.</summary>
    public DiagnosticTerminalViewModel(DiagnosticTerminalService service, ICommandGate gate)
    {
        _service = service ?? throw new ArgumentNullException(nameof(service));
        _gate = gate ?? throw new ArgumentNullException(nameof(gate));

        _readFunctions = new List<TerminalReadChoice>
        {
            new("读取线圈 (FC01)", TerminalReadFunction.Coils),
            new("读取离散输入 (FC02)", TerminalReadFunction.Discrete),
            new("读取保持寄存器 (FC03)", TerminalReadFunction.Holding),
            new("读取输入寄存器 (FC04)", TerminalReadFunction.Input),
        };
        _selectedReadFunction = _readFunctions[2]; // FC03 default.
        _isUnlocked = _service.IsUnlocked;
        _connectionState = ConnectionState.Disconnected;
    }

    /// <summary>The selectable read functions (读取线圈 / 读取离散 / 读取保持 / 读取输入).</summary>
    public IReadOnlyList<TerminalReadChoice> ReadFunctions => _readFunctions;

    /// <summary>True when the link is Online and host writes are permitted (design §5.3).</summary>
    public bool IsOnline => _gate.IsOnline;

    /// <summary>Human-readable link text (在线 / 离线 / …) for the terminal-page header.</summary>
    public string ConnectionStatusText => ConnectionState switch
    {
        ConnectionState.Online => "在线",
        ConnectionState.Connecting => "连接中",
        ConnectionState.Reconnecting => "重连中",
        ConnectionState.HeartbeatLost => "心跳丢失",
        _ => "离线",
    };

    // The unlock checkbox mirrors the service's own guard; the 5-minute auto-lock is applied by the
    // service, so a stale checkbox is refreshed after every operation.
    partial void OnIsUnlockedChanged(bool value)
    {
        _service.SetUnlocked(value);
        WriteRegisterCommand.NotifyCanExecuteChanged();
        WriteCoilCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsBusyChanged(bool value)
    {
        RunReadCommand.NotifyCanExecuteChanged();
        WriteRegisterCommand.NotifyCanExecuteChanged();
        WriteCoilCommand.NotifyCanExecuteChanged();
    }

    // --- Unlock / lock (design §6.5: 解锁后才允许写入，5 分钟自动锁定) -----------------------------------

    /// <summary>Grants the 5-minute write unlock. The terminal auto-locks thereafter (service-side).</summary>
    [RelayCommand]
    private void Unlock()
    {
        IsUnlocked = true;
        StatusText = StateUnlockedText;
        ErrorText = null;
    }

    /// <summary>Locks the terminal immediately (revokes the write unlock).</summary>
    [RelayCommand]
    private void Lock()
    {
        IsUnlocked = false;
        StatusText = "终端已锁定。";
        ErrorText = null;
    }

    // --- Read (always permitted) --------------------------------------------------------------------

    /// <summary>Runs the selected read (FC01/02/03/04) against the entered slave/address/count.</summary>
    [RelayCommand(CanExecute = nameof(CanRead))]
    private async Task RunReadAsync(CancellationToken cancellationToken)
    {
        if (!TryParseReadInputs(out var slaveId, out var address, out var count))
        {
            return;
        }

        var function = SelectedReadFunction?.Function ?? TerminalReadFunction.Holding;
        await ExecuteAsync(cancellationToken,
            async token => function switch
            {
                TerminalReadFunction.Coils => await _service.ReadCoils(slaveId, address, count, token),
                TerminalReadFunction.Discrete => await _service.ReadDiscrete(slaveId, address, count, token),
                TerminalReadFunction.Input => await _service.ReadInput(slaveId, address, count, token),
                _ => await _service.ReadHolding(slaveId, address, count, token),
            },
            "读取完成");
    }

    private bool CanRead() => !IsBusy;

    // --- Write (locked + stop-gated) ---------------------------------------------------------------

    /// <summary>Writes a single holding register (FC06). Requires the unlock and the machine not running.</summary>
    [RelayCommand(CanExecute = nameof(CanWrite))]
    private async Task WriteRegisterAsync(CancellationToken cancellationToken)
    {
        if (!TryParseWriteInputs(out var slaveId, out var address, out var value))
        {
            return;
        }

        await ExecuteAsync(cancellationToken,
            token => _service.WriteRegister(slaveId, address, value, token),
            "写入成功");
    }

    /// <summary>Writes a single coil (FC05). Requires the unlock and the machine not running.</summary>
    [RelayCommand(CanExecute = nameof(CanWrite))]
    private async Task WriteCoilAsync(CancellationToken cancellationToken)
    {
        if (!TryParseCoilInputs(out var slaveId, out var address, out var value))
        {
            return;
        }

        await ExecuteAsync(cancellationToken,
            token => _service.WriteCoil(slaveId, address, value, token),
            "写入成功");
    }

    private bool CanWrite() => IsOnline && IsUnlocked && !IsBusy;

    // --- State application (composition-root wired) --------------------------------------------------

    /// <summary>Applies an observed supervised-link state. Writes stay gated by <see cref="IsOnline"/>
    /// (the injected gate), and the write commands re-query their CanExecute.</summary>
    public void ApplyConnectionState(ConnectionState state)
    {
        ConnectionState = state;
        OnPropertyChanged(nameof(IsOnline));
        WriteRegisterCommand.NotifyCanExecuteChanged();
        WriteCoilCommand.NotifyCanExecuteChanged();
    }

    private static string StateUnlockedText =>
        "已解锁——写入仅 5 分钟内有效，之后自动锁定；机器运行时禁止写入。";
    // --- Command execution --------------------------------------------------------------------------

    private async Task ExecuteAsync(
        CancellationToken cancellationToken,
        Func<CancellationToken, Task<TerminalOpResult>> invoke,
        string successPrefix)
    {
        IsBusy = true;
        try
        {
            var result = await invoke(cancellationToken);
            IsUnlocked = _service.IsUnlocked;
            HexResult = result.Hex;
            ElapsedMs = result.Elapsed.TotalMilliseconds.ToString("0.0");
            if (result.Success)
            {
                StatusText = $"{successPrefix}（耗时 {ElapsedMs} ms）";
                ErrorText = null;
            }
            else
            {
                StatusText = "操作失败";
                ErrorText = result.Error ?? "未知错误";
            }
        }
        catch (OperationCanceledException)
        {
            StatusText = "操作已取消";
            ErrorText = null;
        }
        catch (Exception ex)
        {
            // A transport/command failure must never escape to the AsyncRelayCommand (it would surface on
            // the UI thread): report it on the status line instead and keep the UI alive.
            StatusText = "操作失败";
            ErrorText = ex.Message;
        }
        finally
        {
            IsBusy = false;
            WriteRegisterCommand.NotifyCanExecuteChanged();
            WriteCoilCommand.NotifyCanExecuteChanged();
            RunReadCommand.NotifyCanExecuteChanged();
        }
    }

    // --- Input parsing (never throws; failures land on StatusText/ErrorText) ------------------------

    private bool TryParseReadInputs(out byte slaveId, out ushort address, out ushort count)
    {
        slaveId = 0;
        address = 0;
        count = 0;

        if (!TryParseSlaveId(out slaveId))
        {
            return false;
        }

        if (!TryParseAddress(out address))
        {
            return false;
        }

        if (!ushort.TryParse(Count?.Trim(), out count))
        {
            StatusText = "操作失败";
            ErrorText = "数量必须是 1..65535 的整数。";
            return false;
        }

        return true;
    }

    private bool TryParseWriteInputs(out byte slaveId, out ushort address, out ushort value)
    {
        slaveId = 0;
        address = 0;
        value = 0;

        if (!TryParseSlaveId(out slaveId) || !TryParseAddress(out address))
        {
            return false;
        }

        if (!ushort.TryParse(Value?.Trim(), out value))
        {
            StatusText = "操作失败";
            ErrorText = "寄存器值必须是 0..65535 的整数。";
            return false;
        }

        return true;
    }

    private bool TryParseCoilInputs(out byte slaveId, out ushort address, out bool value)
    {
        slaveId = 0;
        address = 0;
        value = false;

        if (!TryParseSlaveId(out slaveId) || !TryParseAddress(out address))
        {
            return false;
        }

        if (!bool.TryParse(Value?.Trim(), out value))
        {
            StatusText = "操作失败";
            ErrorText = "线圈值必须是 true/false（或真/假）。";
            return false;
        }

        return true;
    }

    private bool TryParseSlaveId(out byte slaveId)
    {
        slaveId = 0;
        if (!byte.TryParse(SlaveId?.Trim(), out slaveId))
        {
            StatusText = "操作失败";
            ErrorText = "站号必须是 1..247 的整数。";
            return false;
        }

        return true;
    }

    private bool TryParseAddress(out ushort address)
    {
        address = 0;
        if (!ushort.TryParse(Address?.Trim(), out address))
        {
            StatusText = "操作失败";
            ErrorText = "地址必须是 0..65535 的整数。";
            return false;
        }

        return true;
    }
}
