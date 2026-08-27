using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PlcSoftware.App.Services;
using PlcSoftware.Core.Abstractions;
using PlcSoftware.Core.Configuration;
using PlcSoftware.Core.Models;

namespace PlcSoftware.App.ViewModels;

/// <summary>
/// The communication-settings page (design §6.8): edit the serial options (串口/波特率/数据位/校验/停止位/站号/
/// 超时/重试), validate them through <see cref="SerialConnectionOptions.Validate"/>, and run a connection test
/// behind an explicit user action. The production transport is NOT wired here (the demo ships on the in-memory
/// simulation) — the page only validates the options and, when the operator explicitly triggers 测试连接, probes
/// the configured port through the injected <see cref="IConnectionTester"/> (the <c>SerialPortFactory</c> seam).
///
/// <para><b>Online → disconnect first (design §6.8 修改配置前先断开当前连接).</b> While the supervised link is
/// <see cref="ConnectionState.Online"/> the editable form (<see cref="IsFormEnabled"/>) and the connection-test
/// command are both disabled and <see cref="OnlineEditHintText"/> shows
/// <c>在线时修改配置必须先断开连接。</c>. Only <see cref="ConnectionState.Online"/> blocks the form; every other
/// link state (disconnected / connecting / reconnecting / heartbeat lost) leaves it editable because a connect
/// that is not yet Online is not a live session.</para>
///
/// <para><b>Validation.</b> Every form-field change re-parses the fields into a temporary
/// <see cref="SerialConnectionOptions"/> and runs <see cref="SerialConnectionOptions.Validate"/> (plus a
/// non-numeric check on the numeric fields). The errors surface on <see cref="ValidationText"/>; a connection
/// test is never attempted against an invalid configuration.</para>
///
/// <para><b>Connection test.</b> <see cref="TestConnectionCommand"/> is cancellable — an in-flight test can be
/// cancelled (the command's own <c>IAsyncRelayCommand.Cancel()</c> flows into the tester's token) and reports
/// 连接测试已取消 without surfacing a bogus failure.</para>
///
/// <para><b>Offline-phase edits never propagate to a running transport.</b> Editing these fields only mutates the
/// displayed options and the validation state; it never reconfigures the live transport. A transport that is
/// already <see cref="ConnectionState.Online"/> stays on its original connection settings, and the edited values
/// take effect only after the operator disconnects, reconnects and re-runs the connection test. There is no
/// hot-reconfiguration path, so the page cannot silently change the parameters of a session that is producing
/// data (design §6.8: 修改配置前先断开当前连接 — the online form is locked precisely so this is enforced).</para>
///
/// <para><b>No UI-thread dependency.</b> The view model consumes <see cref="ConnectionState"/> through
/// <see cref="ApplyConnectionState"/> and runs the test through the injected <see cref="IConnectionTester"/>. It
/// never touches a <c>Dispatcher</c> or any WPF type, so it stays testable under a pure unit test host (the App
/// tests are CI-only on Windows because the WindowsDesktop runtime cannot run on the WSL cross-build, not
/// because this class needs WPF).</para>
/// </summary>
public sealed partial class ConnectionSettingsViewModel : ObservableObject
{
    private readonly IConnectionTester _tester;
    private readonly ISupervisedConnection? _supervisor;

    /// <summary>The supervised link state, used to block config editing while online (design §6.8).</summary>
    [ObservableProperty]
    private ConnectionState _connectionState;

    [ObservableProperty]
    private string _portName = "COM1";

    [ObservableProperty]
    private string _baudRateText = "9600";

    [ObservableProperty]
    private int _dataBits = 8;

    [ObservableProperty]
    private Parity _selectedParity = Parity.None;

    [ObservableProperty]
    private StopBits _selectedStopBits = StopBits.One;

    [ObservableProperty]
    private string _slaveIdText = "1";

    [ObservableProperty]
    private string _timeoutMsText = "1000";

    [ObservableProperty]
    private string _retriesText = "3";

    /// <summary>The joined validation errors, or empty when the configuration is valid.</summary>
    [ObservableProperty]
    private string _validationText = string.Empty;

    /// <summary>The outcome of the last connection test (成功 / 失败 / 已取消 / 配置无效), or empty.</summary>
    [ObservableProperty]
    private string _testResultText = string.Empty;

    /// <summary>True while a connection test is in flight.</summary>
    [ObservableProperty]
    private bool _isTesting;

    /// <summary>Builds the settings page over the injected connection tester, the supervised connection and the
    /// initial options (defaults loaded from <c>appsettings.json</c> at the composition root). The supervised
    /// connection is optional so consumers without a live link (unit tests) can still edit/test options; at the
    /// composition root it is always supplied, which is what enables the 断开连接 affordance.</summary>
    public ConnectionSettingsViewModel(IConnectionTester tester, ISupervisedConnection? supervisor = null, SerialConnectionOptions? initial = null)
    {
        _tester = tester ?? throw new ArgumentNullException(nameof(tester));
        _supervisor = supervisor;
        if (initial is not null)
        {
            PortName = initial.PortName;
            BaudRateText = initial.BaudRate.ToString();
            DataBits = initial.DataBits;
            SelectedParity = initial.Parity;
            SelectedStopBits = initial.StopBits;
            SlaveIdText = initial.SlaveId.ToString();
            TimeoutMsText = initial.TimeoutMs.ToString();
            RetriesText = initial.Retries.ToString();
        }

        _connectionState = ConnectionState.Disconnected;
        ParityOptionsText = ParityOptions.Select(ParityText).ToArray();
        StopBitsOptionsText = StopBitsOptions.Select(StopBitsText).ToArray();
        RecomputeValidation();
    }

    /// <summary>The selectable data-bit counts (5/6/7/8).</summary>
    public IReadOnlyList<int> DataBitsOptions { get; } = new[] { 5, 6, 7, 8 };

    /// <summary>The selectable parity settings.</summary>
    public IReadOnlyList<Parity> ParityOptions { get; } = new[] { Parity.None, Parity.Odd, Parity.Even, Parity.Mark, Parity.Space };

    /// <summary>The selectable stop-bits settings (None is not a real serial setting, so it is omitted).</summary>
    public IReadOnlyList<StopBits> StopBitsOptions { get; } = new[] { StopBits.One, StopBits.Two, StopBits.OnePointFive };

    /// <summary>True only when the supervised link is <see cref="ConnectionState.Online"/>.</summary>
    public bool IsOnline => ConnectionState == ConnectionState.Online;

    /// <summary>False while online so the operator must disconnect before editing the config (design §6.8).</summary>
    public bool IsFormEnabled => !IsOnline;

    /// <summary>The hint shown while online: 在线时修改配置必须先断开连接。</summary>
    public string OnlineEditHintText => IsOnline ? "在线时修改配置必须先断开连接。" : string.Empty;

    /// <summary>True while online and no connection test is in flight, so the 断开连接 affordance is usable.
    /// A test holds the port, so the operator cannot disconnect mid-probe.</summary>
    public bool CanDisconnect => IsOnline && !IsTesting;

    /// <summary>The Chinese display label for a parity setting (无 / 奇校验 / 偶校验 / 标志 / 空格).</summary>
    public string ParityText(Parity parity) => parity switch
    {
        Parity.None => "无",
        Parity.Odd => "奇校验",
        Parity.Even => "偶校验",
        Parity.Mark => "标志",
        Parity.Space => "空格",
        _ => parity.ToString(),
    };

    /// <summary>The Chinese display label for a stop-bits setting (1 / 1.5 / 2).</summary>
    public string StopBitsText(StopBits stopBits) => stopBits switch
    {
        StopBits.One => "1",
        StopBits.Two => "2",
        StopBits.OnePointFive => "1.5",
        _ => stopBits.ToString(),
    };

    /// <summary>The Chinese display labels aligned with <see cref="ParityOptions"/> (order preserving).</summary>
    public IReadOnlyList<string> ParityOptionsText { get; }

    /// <summary>The Chinese display labels aligned with <see cref="StopBitsOptions"/> (order preserving).</summary>
    public IReadOnlyList<string> StopBitsOptionsText { get; }

    /// <summary>Human-readable link text (在线 / 离线 / …) for the page header.</summary>
    public string ConnectionStatusText => ConnectionState switch
    {
        ConnectionState.Online => "在线",
        ConnectionState.Connecting => "连接中",
        ConnectionState.Reconnecting => "重连中",
        ConnectionState.HeartbeatLost => "心跳丢失",
        _ => "离线",
    };

    /// <summary>Builds a <see cref="SerialConnectionOptions"/> from the current form fields (best-effort parse:
    /// an unparseable numeric field keeps the recorded default, which the validation catches).</summary>
    public SerialConnectionOptions BuildOptions()
    {
        var options = new SerialConnectionOptions
        {
            PortName = (PortName ?? string.Empty).Trim(),
            DataBits = DataBits,
            Parity = SelectedParity,
            StopBits = SelectedStopBits,
        };

        if (int.TryParse(BaudRateText?.Trim(), out var baudRate))
        {
            options.BaudRate = baudRate;
        }

        if (byte.TryParse(SlaveIdText?.Trim(), out var slaveId))
        {
            options.SlaveId = slaveId;
        }

        if (int.TryParse(TimeoutMsText?.Trim(), out var timeoutMs))
        {
            options.TimeoutMs = timeoutMs;
        }

        if (int.TryParse(RetriesText?.Trim(), out var retries))
        {
            options.Retries = retries;
        }

        return options;
    }

    /// <summary>Validates the current form. Non-numeric numeric fields are rejected and the remaining errors come
    /// from <see cref="SerialConnectionOptions.Validate"/>.</summary>
    public IReadOnlyList<string> BuildValidationErrors()
    {
        var errors = new List<string>();

        if (!int.TryParse(BaudRateText?.Trim(), out _))
        {
            errors.Add("baudRate 必须为整数。");
        }

        if (!byte.TryParse(SlaveIdText?.Trim(), out _))
        {
            errors.Add("slaveId 必须为 1-247 的整数。");
        }

        if (!int.TryParse(TimeoutMsText?.Trim(), out _))
        {
            errors.Add("timeout 必须为非负整数。");
        }

        if (!int.TryParse(RetriesText?.Trim(), out _))
        {
            errors.Add("retries 必须为非负整数。");
        }

        errors.AddRange(BuildOptions().Validate());
        return errors;
    }

    // --- Connection test (design §6.8: 支持连接测试, behind explicit user action) ------------------------

    [RelayCommand(CanExecute = nameof(CanTestConnection))]
    private async Task TestConnectionAsync(CancellationToken cancellationToken)
    {
        // Set the in-flight flag synchronously before the first await so a double-click cannot start a
        // second probe. The test hint on the page flips the moment the command begins to run.
        IsTesting = true;

        var errors = BuildValidationErrors();
        if (errors.Count > 0)
        {
            TestResultText = $"配置无效：{string.Join("；", errors)}";
            IsTesting = false;
            return;
        }

        try
        {
            await _tester.TestAsync(BuildOptions(), cancellationToken);
            TestResultText = "连接成功";
        }
        catch (TimeoutException)
        {
            // The tester bounds the probe with its own timeout (see IConnectionTester); a port that
            // never opens surfaces as a bounded timeout, not as an unhandled exception.
            TestResultText = "连接测试超时";
        }
        catch (OperationCanceledException)
        {
            // A cancel must surface as a deliberate cancel, not as a bogus transport failure.
            TestResultText = "连接测试已取消";
        }
        catch (Exception ex)
        {
            // A transport/probe failure must never escape to the AsyncRelayCommand (it would surface on the UI
            // thread): report it on the result line instead and keep the UI alive.
            TestResultText = $"连接失败:{ex.Message}";
        }
        finally
        {
            IsTesting = false;
        }
    }

    /// <summary>The test is a config action, so it requires an offline link (design §6.8) and no test in flight.</summary>
    private bool CanTestConnection() => !IsOnline && !IsTesting;

    // --- Disconnect affordance (design §6.8: 修改配置前先断开当前连接) -----------------------------------

    // While the form is locked (Online) the only way back to an editable config is to tear the supervised
    // link down. Without this affordance the page would dead-end: it tells the operator 在线时修改配置必须先断开连接
    // but offers no way to disconnect. CanDisconnect is false both when offline (nothing to disconnect) and
    // while a test holds the port (design §6.8).
    [RelayCommand(CanExecute = nameof(CanDisconnect))]
    private async Task DisconnectAsync(CancellationToken cancellationToken)
    {
        if (!CanDisconnect || _supervisor is null)
        {
            return;
        }

        try
        {
            await _supervisor.DisconnectAsync(cancellationToken);
            // Reflect a successful disconnect so the form unlocks immediately; the composition root also
            // broadcasts the supervisor's own StateChanged, which would reach here on the UI thread anyway.
            ApplyConnectionState(ConnectionState.Disconnected);
        }
        catch (OperationCanceledException)
        {
            // A cancelled disconnect leaves the link as it was — keep the online lock.
        }
        catch (Exception)
        {
            // A failed tear-down leaves the link up (it may be a transient transport error); the operator
            // can retry. Never let it escape to the AsyncRelayCommand.
        }
    }

    // --- State application (composition-root wired) ----------------------------------------------------

    /// <summary>Applies an observed supervised-link state, refreshing the form lock and the CanExecute
    /// conditions for the test and disconnect commands.</summary>
    public void ApplyConnectionState(ConnectionState state)
    {
        ConnectionState = state;
        OnPropertyChanged(nameof(IsOnline));
        OnPropertyChanged(nameof(IsFormEnabled));
        OnPropertyChanged(nameof(OnlineEditHintText));
        OnPropertyChanged(nameof(CanDisconnect));
        TestConnectionCommand.NotifyCanExecuteChanged();
        DisconnectCommand.NotifyCanExecuteChanged();
    }

    // --- Change handling --------------------------------------------------------------------------------

    private void OnSerialFieldChanged()
    {
        RecomputeValidation();
        TestConnectionCommand.NotifyCanExecuteChanged();
    }

    partial void OnPortNameChanged(string value) => OnSerialFieldChanged();
    partial void OnBaudRateTextChanged(string value) => OnSerialFieldChanged();
    partial void OnDataBitsChanged(int value) => OnSerialFieldChanged();
    partial void OnSelectedParityChanged(Parity value) => OnSerialFieldChanged();
    partial void OnSelectedStopBitsChanged(StopBits value) => OnSerialFieldChanged();
    partial void OnSlaveIdTextChanged(string value) => OnSerialFieldChanged();
    partial void OnTimeoutMsTextChanged(string value) => OnSerialFieldChanged();
    partial void OnRetriesTextChanged(string value) => OnSerialFieldChanged();

    partial void OnIsTestingChanged(bool value)
    {
        TestConnectionCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CanDisconnect));
        DisconnectCommand.NotifyCanExecuteChanged();
    }

    partial void OnConnectionStateChanged(ConnectionState value) => OnPropertyChanged(nameof(ConnectionStatusText));

    private void RecomputeValidation()
    {
        var errors = BuildValidationErrors();
        ValidationText = errors.Count == 0 ? string.Empty : string.Join("；", errors);
    }
}
