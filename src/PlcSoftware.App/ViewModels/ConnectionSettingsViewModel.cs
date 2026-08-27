using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PlcSoftware.App.Services;
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
/// <para><b>No UI-thread dependency.</b> The view model consumes <see cref="ConnectionState"/> through
/// <see cref="ApplyConnectionState"/> and runs the test through the injected <see cref="IConnectionTester"/>. It
/// never touches a <c>Dispatcher</c> or any WPF type, so it stays testable under a pure unit test host (the App
/// tests are CI-only on Windows because the WindowsDesktop runtime cannot run on the WSL cross-build, not
/// because this class needs WPF).</para>
/// </summary>
public sealed partial class ConnectionSettingsViewModel : ObservableObject
{
    private readonly IConnectionTester _tester;

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

    /// <summary>Builds the settings page over the injected connection tester and the initial options
    /// (defaults loaded from <c>appsettings.json</c> at the composition root).</summary>
    public ConnectionSettingsViewModel(IConnectionTester tester, SerialConnectionOptions? initial = null)
    {
        _tester = tester ?? throw new ArgumentNullException(nameof(tester));

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
        var errors = BuildValidationErrors();
        if (errors.Count > 0)
        {
            TestResultText = $"配置无效：{string.Join("；", errors)}";
            return;
        }

        IsTesting = true;
        try
        {
            await _tester.TestAsync(BuildOptions(), cancellationToken);
            TestResultText = "连接成功";
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

    // --- State application (composition-root wired) ----------------------------------------------------

    /// <summary>Applies an observed supervised-link state, refreshing the form lock and the test CanExecute.</summary>
    public void ApplyConnectionState(ConnectionState state)
    {
        ConnectionState = state;
        OnPropertyChanged(nameof(IsOnline));
        OnPropertyChanged(nameof(IsFormEnabled));
        OnPropertyChanged(nameof(OnlineEditHintText));
        TestConnectionCommand.NotifyCanExecuteChanged();
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

    partial void OnIsTestingChanged(bool value) => TestConnectionCommand.NotifyCanExecuteChanged();

    partial void OnConnectionStateChanged(ConnectionState value) => OnPropertyChanged(nameof(ConnectionStatusText));

    private void RecomputeValidation()
    {
        var errors = BuildValidationErrors();
        ValidationText = errors.Count == 0 ? string.Empty : string.Join("；", errors);
    }
}
