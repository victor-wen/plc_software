using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PlcSoftware.Core.Abstractions;
using PlcSoftware.Core.Models;
using PlcSoftware.Core.Services;

namespace PlcSoftware.App.ViewModels;

/// <summary>
/// One editable engineering parameter (design §6.5): D201 调宽速度 / D202 目标宽度 / D204 脉冲当量 /
/// D205 皮带速度. It owns a single write attempt — the integer input, the range hint, the pending
/// confirmation (old → new, unit, allowed range) and the read-back <see cref="ResultText"/> — so the page
/// can host an ItemsControl of independent rows. The parameter and its configured range come from the
/// injected <see cref="ParameterDefinition"/> (工程配置); a definition with no configured Min/Max shows
/// "未配置范围" and the write is refused downstream by <see cref="ParameterService"/> (design §4.3:
/// 上下限未配置或配置非法时禁止写入).
/// </summary>
public sealed partial class ParameterEditor : ObservableObject
{
    private readonly ParameterDefinition _definition;

    public ParameterEditor(ParameterDefinition definition)
    {
        _definition = definition ?? throw new ArgumentNullException(nameof(definition));
    }

    /// <summary>The logical PLC address, e.g. "D201".</summary>
    public string Name => _definition.Name;

    /// <summary>The engineering unit, e.g. "Hz" or "mm".</summary>
    public string Unit => _definition.Unit;

    /// <summary>Configured lower bound (null = not yet configured).</summary>
    public int? Min => _definition.Min;

    /// <summary>Configured upper bound (null = not yet configured).</summary>
    public int? Max => _definition.Max;

    /// <summary>Human-readable allowed range (design §6.5: 写入前显示…允许范围).</summary>
    public string RangeHintText => Min.HasValue && Max.HasValue
        ? $"{Min} ~ {Max} {Unit}"
        : "未配置范围";

    /// <summary>The current PLC value read from the latest snapshot (the 旧值 shown before a write).</summary>
    [ObservableProperty]
    private int? _oldValue;

    /// <summary>The raw integer text the operator typed (bound to the input TextBox).</summary>
    [ObservableProperty]
    private string _inputText = string.Empty;

    /// <summary>Input/range validation error, or null when the input is a valid in-range integer.</summary>
    [ObservableProperty]
    private string? _error;

    /// <summary>The read-back outcome of the last write (Success / Mismatch / Unknown / Rejected).</summary>
    [ObservableProperty]
    private string? _resultText;

    /// <summary>True once a valid value is pending the operator's confirmation.</summary>
    [ObservableProperty]
    private bool _isPending;

    /// <summary>The parsed value awaiting confirmation (design §6.5: 新值).</summary>
    [ObservableProperty]
    private int _pendingValue;

    /// <summary>The confirmation prompt: old value → new value, unit and allowed range (design §6.5:
    /// 写入前显示旧值、新值、单位和允许范围).</summary>
    public string ConfirmationText =>
        $"{Name}：{(OldValue?.ToString() ?? "—")} → {PendingValue} {Unit}（允许范围 {RangeHintText}）";

    partial void OnOldValueChanged(int? value) => OnPropertyChanged(nameof(ConfirmationText));

    partial void OnPendingValueChanged(int value) => OnPropertyChanged(nameof(ConfirmationText));

    partial void OnIsPendingChanged(bool value) => OnPropertyChanged(nameof(ConfirmationText));
}

/// <summary>
/// One read-only engineering value displayed on the parameter page (design §6.5): D203 当前宽度,
/// D210 调宽差值 and the D212.D213 调宽脉冲数 composite. The value is read straight from the decoded
/// snapshot (the point map marks these 只读) — they are never writable and carry no write flow.
/// </summary>
public sealed partial class ReadOnlyParameter : ObservableObject
{
    public ReadOnlyParameter(string key, string label, string unit)
    {
        Key = key ?? throw new ArgumentNullException(nameof(key));
        Label = label ?? throw new ArgumentNullException(nameof(label));
        Unit = unit ?? throw new ArgumentNullException(nameof(unit));
    }

    /// <summary>The snapshot key: "D203", "D210" or RegisterDecoder.WidthPulseCountKey ("D212.D213").</summary>
    public string Key { get; }

    /// <summary>The human-readable name (当前宽度 / 调宽差值 / 调宽脉冲数).</summary>
    public string Label { get; }

    /// <summary>The engineering unit (mm / 脉冲), empty for a dimensionless delta.</summary>
    public string Unit { get; }

    /// <summary>The formatted read-only value, or null when the snapshot has not reported it yet.</summary>
    [ObservableProperty]
    private string? _valueText;
}

/// <summary>
/// The parameter page (design §6.5): editable D201/D202/D204/D205 and read-only D203/D210/D212.D213.
///
/// <para><b>Write flow (design §6.5).</b> The operator types an integer into an editor's
/// <see cref="ParameterEditor.InputText"/> and presses 写入, which runs <see cref="PrepareWriteCommand"/>:
/// the text is parsed (a non-integer input is rejected with an error) and range-checked against the
/// configured <see cref="ParameterEditor.Min"/>/<see cref="ParameterEditor.Max"/> (an out-of-range value is
/// rejected). A valid value sets the editor <see cref="ParameterEditor.IsPending"/> and surfaces the
/// confirmation prompt (<see cref="ParameterEditor.ConfirmationText"/>: 旧值 → 新值 + 单位 + 允许范围). The
/// operator confirms via <see cref="ConfirmWriteCommand"/> which routes to the injected
/// <see cref="ParameterService"/> and reports the read-back outcome (<see cref="ParameterWriteStatus"/>
/// Success / Mismatch / Unknown / Rejected) on <see cref="ParameterEditor.ResultText"/>. A Mismatch or
/// Unknown read-back keeps the original value (the editor's <see cref="ParameterEditor.OldValue"/> is only
/// ever driven by the snapshot, never by a write) and records the reason (design §6.5: 写回失败时保留原值并
/// 记录原因). Cancelling a pending write via <see cref="CancelWriteCommand"/> simply drops the prompt.</para>
///
/// <para><b>Duplicate-write guard (design §6.5 save-in-progress).</b> While a write is in flight
/// <see cref="IsSaving"/> is <c>true</c> and the confirm button's <c>CanExecute</c> is <c>false</c>, so a
/// rapid double-click cannot fire two writes for the same parameter.</para>
///
/// <para><b>Offline (design §5.3).</b> <see cref="IsOnline"/> gates the write/confirm buttons; the injected
/// <see cref="ParameterService"/> independently rejects any write while the link is offline.</para>
///
/// <para><b>No UI-thread dependency.</b> The view model consumes Core snapshots + the supervised link state
/// through <see cref="ApplySnapshot"/> / <see cref="ApplyConnectionState"/> and executes writes through the
/// injected <see cref="ParameterService"/>. It never touches a <c>Dispatcher</c> or any WPF type, so it stays
/// testable under a pure unit test host (the App tests are CI-only on Windows because the WindowsDesktop
/// runtime cannot run on the WSL cross-build, not because this class needs WPF).</para>
/// </summary>
public sealed partial class ParametersViewModel : ObservableObject
{
    private readonly ParameterService _service;
    private readonly ICommandGate _gate;
    private readonly List<ParameterEditor> _editors = new();
    private readonly List<ReadOnlyParameter> _readonlyItems = new();
    private ParameterEditor? _pendingEditor;

    /// <summary>The supervised link state, used only for the connection-status text (design §6.1).</summary>
    [ObservableProperty]
    private ConnectionState _connectionState;

    /// <summary>True while a parameter write is in flight (design §6.5 save-in-progress guard).</summary>
    [ObservableProperty]
    private bool _isSaving;

    /// <summary>Builds the parameter page over the injected parameter service, command gate and the
    /// configured writable parameter definitions (工程配置 ranges).</summary>
    public ParametersViewModel(
        ParameterService parameterService,
        ICommandGate gate,
        IEnumerable<ParameterDefinition> writableParameters)
    {
        _service = parameterService ?? throw new ArgumentNullException(nameof(parameterService));
        _gate = gate ?? throw new ArgumentNullException(nameof(gate));
        if (writableParameters is null)
        {
            throw new ArgumentNullException(nameof(writableParameters));
        }

        foreach (var definition in writableParameters)
        {
            _editors.Add(new ParameterEditor(definition));
        }

        _readonlyItems.Add(new ReadOnlyParameter("D203", "当前宽度", "mm"));
        _readonlyItems.Add(new ReadOnlyParameter("D210", "调宽差值", string.Empty));
        _readonlyItems.Add(new ReadOnlyParameter(RegisterDecoder.WidthPulseCountKey, "调宽脉冲数", "脉冲"));

        _connectionState = ConnectionState.Disconnected;
    }

    /// <summary>The editable writable parameters (D201/D202/D204/D205).</summary>
    public IReadOnlyList<ParameterEditor> WritableParameters => _editors;

    /// <summary>The read-only parameters displayed as-is (D203/D210/D212.D213).</summary>
    public IReadOnlyList<ReadOnlyParameter> ReadOnlyParameters => _readonlyItems;

    /// <summary>True when the link is Online and host writes are permitted (design §5.3).</summary>
    public bool IsOnline => _gate.IsOnline;

    /// <summary>True while a write is awaiting the operator's confirmation.</summary>
    public bool IsPending => _pendingEditor is not null;

    /// <summary>Human-readable link text (在线 / 离线 / …) for the parameter-page header.</summary>
    public string ConnectionStatusText => ConnectionState switch
    {
        ConnectionState.Online => "在线",
        ConnectionState.Connecting => "连接中",
        ConnectionState.Reconnecting => "重连中",
        ConnectionState.HeartbeatLost => "心跳丢失",
        _ => "离线",
    };

    // --- Write flow (design §6.5: validate input → show old/new/unit/range → confirm → ParameterService) ----

    /// <summary>Validates the integer input and, when valid, stages the confirmation prompt.</summary>
    [RelayCommand(CanExecute = nameof(CanPrepareWrite))]
    private void PrepareWrite(ParameterEditor editor)
    {
        if (editor is null)
        {
            throw new ArgumentNullException(nameof(editor));
        }

        ClearPending();

        // Integer input (design §6.5 整数输入): a non-integer is rejected without a write.
        if (!int.TryParse(editor.InputText?.Trim(), out var value))
        {
            editor.Error = "请输入整数。";
            return;
        }

        // Range check against the configured limits (design §6.5: 写入前显示允许范围).
        if (editor.Min.HasValue && editor.Max.HasValue && (value < editor.Min || value > editor.Max))
        {
            editor.Error = $"超出允许范围 {editor.Min} ~ {editor.Max} {editor.Unit}。";
            return;
        }

        editor.Error = null;
        editor.PendingValue = value;
        editor.IsPending = true;
        _pendingEditor = editor;
        OnPropertyChanged(nameof(IsPending));
        ConfirmWriteCommand.NotifyCanExecuteChanged();
        CancelWriteCommand.NotifyCanExecuteChanged();
    }

    private bool CanPrepareWrite(ParameterEditor editor) => IsOnline && !IsSaving;

    /// <summary>Confirms the staged write and reports the read-back outcome.</summary>
    [RelayCommand(CanExecute = nameof(CanConfirmWrite))]
    private async Task ConfirmWriteAsync(CancellationToken cancellationToken)
    {
        var editor = _pendingEditor;
        if (editor is null)
        {
            return;
        }

        IsSaving = true;
        try
        {
            var result = await _service.WriteAsync(editor.Name, editor.PendingValue, cancellationToken);
            editor.ResultText = FormatWriteResult(result);
        }
        catch (Exception ex)
        {
            // A transport/command failure must never escape to the AsyncRelayCommand (it would surface on
            // the UI thread): report it on the result line instead and keep the UI alive.
            editor.ResultText = $"写入失败：{ex.Message}";
        }
        finally
        {
            IsSaving = false;
            editor.IsPending = false;
            _pendingEditor = null;
            OnPropertyChanged(nameof(IsPending));
            ConfirmWriteCommand.NotifyCanExecuteChanged();
            CancelWriteCommand.NotifyCanExecuteChanged();
        }
    }

    private bool CanConfirmWrite() => IsOnline && !IsSaving && IsPending;

    /// <summary>Drops a pending write without sending it.</summary>
    [RelayCommand(CanExecute = nameof(CanCancelWrite))]
    private void CancelWrite() => ClearPending();

    private bool CanCancelWrite() => IsPending && !IsSaving;

    // --- State application (composition-root wired) --------------------------------------------------

    /// <summary>Applies an observed supervised-link state. Writes stay gated by <see cref="IsOnline"/>
    /// (the injected gate), and the write commands re-query their CanExecute.</summary>
    public void ApplyConnectionState(ConnectionState state)
    {
        ConnectionState = state;
        OnPropertyChanged(nameof(IsOnline));
        PrepareWriteCommand.NotifyCanExecuteChanged();
        ConfirmWriteCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Applies one decoded snapshot: refreshes every editable parameter's current (old) value
    /// (D201/D202/D204/D205) and every read-only display value (D203/D210/D212.D213).</summary>
    public void ApplySnapshot(DeviceSnapshot snapshot)
    {
        if (snapshot is null)
        {
            throw new ArgumentNullException(nameof(snapshot));
        }

        var values = snapshot.Values;
        foreach (var editor in _editors)
        {
            editor.OldValue = ReadInt(values, editor.Name);
        }

        foreach (var item in _readonlyItems)
        {
            item.ValueText = ReadDisplayValue(values, item.Key);
        }
    }

    // --- Helpers ---------------------------------------------------------------------------------------

    private void ClearPending()
    {
        if (_pendingEditor is not null)
        {
            _pendingEditor.IsPending = false;
        }

        _pendingEditor = null;
        OnPropertyChanged(nameof(IsPending));
        ConfirmWriteCommand.NotifyCanExecuteChanged();
        CancelWriteCommand.NotifyCanExecuteChanged();
    }

    private static string FormatWriteResult(ParameterWriteResult result)
        => result.Status switch
        {
            ParameterWriteStatus.Success => $"{result.Parameter}写入成功（已读回 {result.ReadBack}）",
            ParameterWriteStatus.Rejected => string.IsNullOrEmpty(result.Message)
                ? $"{result.Parameter}写入被拒绝"
                : $"{result.Parameter}写入被拒绝：{result.Message}",
            ParameterWriteStatus.Mismatch => string.IsNullOrEmpty(result.Message)
                ? $"{result.Parameter}写入不一致（已保留原值）"
                : $"{result.Parameter}写入不一致：{result.Message}（已保留原值）",
            ParameterWriteStatus.Unknown => string.IsNullOrEmpty(result.Message)
                ? $"{result.Parameter}写入结果未知（已保留原值）"
                : $"{result.Parameter}写入结果未知：{result.Message}（已保留原值）",
            _ => string.Empty,
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

    private static string? ReadDisplayValue(IReadOnlyDictionary<string, object?> values, string key)
        => values.TryGetValue(key, out var value)
            ? value switch
            {
                ushort u => u.ToString(),
                uint ui => ui.ToString(),
                int i => i.ToString(),
                short s => s.ToString(),
                byte b => b.ToString(),
                _ => value?.ToString(),
            }
            : null;

    partial void OnIsSavingChanged(bool value)
    {
        PrepareWriteCommand.NotifyCanExecuteChanged();
        ConfirmWriteCommand.NotifyCanExecuteChanged();
        CancelWriteCommand.NotifyCanExecuteChanged();
    }

    partial void OnConnectionStateChanged(ConnectionState value) => OnPropertyChanged(nameof(ConnectionStatusText));
}
