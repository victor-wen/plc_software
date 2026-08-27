using System.IO;
using PlcSoftware.App.Services;
using PlcSoftware.App.ViewModels;
using PlcSoftware.Core.Abstractions;
using PlcSoftware.Core.Configuration;
using PlcSoftware.Core.Models;

namespace PlcSoftware.App.Tests.ViewModels;

/// <summary>
/// Pins how <see cref="ConnectionSettingsViewModel"/> drives the communication-settings page (design §6.8):
/// the serial options are validated through <see cref="SerialConnectionOptions.Validate"/> and surfaced, the
/// form is locked while the link is online (修改配置前先断开当前连接), the operator must disconnect before editing
/// the config, and the connection test runs behind an explicit user action with cancellation.
///
/// <para><b>Online → disconnect first (design §6.8).</b> While the supervised link is
/// <see cref="ConnectionState.Online"/> the editable form (<see cref="ConnectionSettingsViewModel.IsFormEnabled"/>)
/// and the test-connection command are both disabled and the hint
/// <c>在线时修改配置必须先断开连接。</c> is shown; the operator must disconnect (via
/// <see cref="ConnectionSettingsViewModel.DisconnectCommand"/>, which calls the injected
/// <see cref="ISupervisedConnection"/>.DisconnectAsync) before editing the config.
/// </para>
///
/// <para><b>Validation (design §6.8).</b> The form fields are re-parsed into a temporary
/// <see cref="SerialConnectionOptions"/> and validated whenever any field changes. The resulting errors
/// surface on <see cref="ConnectionSettingsViewModel.ValidationText"/>; a connection test is <em>not</em>
/// attempted against an invalid configuration.</para>
///
/// <para><b>Connection test (design §6.8).</b> The test is behind <see cref="ConnectionSettingsViewModel.TestConnectionCommand"/>,
/// routed to the injected <see cref="IConnectionTester"/> (the production implementation probes the configured
/// serial port through the <c>SerialPortFactory</c> seam). The command is cancellable: cancelling aborts the
/// test and reports 连接测试已取消.</para>
///
/// <para><b>No WPF dependency.</b> The view model consumes <see cref="ConnectionState"/> through
/// <see cref="ConnectionSettingsViewModel.ApplyConnectionState"/> and runs the test through the injected
/// <see cref="IConnectionTester"/>; it never touches a <c>Dispatcher</c> or any WPF type, so it stays a pure
/// unit test host (the App tests are CI-only on Windows; on Linux it only contributes a compile RED/GREEN check).</para>
/// </summary>
public class ConnectionSettingsViewModelTests
{
    // --- Serial parameter validation (design §6.8) --------------------------------------------------

    [Fact]
    public void Invalid_serial_values_produce_validation_errors()
    {
        var vm = Build();

        vm.BaudRateText = "-5";
        vm.DataBits = 4;
        vm.SelectedParity = (Parity)999;
        vm.SelectedStopBits = StopBits.None;

        Assert.Contains("baudRate", vm.ValidationText);
        Assert.Contains("dataBits", vm.ValidationText);
        Assert.Contains("parity", vm.ValidationText);
        Assert.Contains("stopBits", vm.ValidationText);
    }

    [Fact]
    public void Valid_serial_values_produce_no_validation_errors()
    {
        var vm = Build();

        Assert.Equal(string.Empty, vm.ValidationText);
        Assert.Empty(vm.BuildValidationErrors());
    }

    [Fact]
    public void Non_numeric_fields_are_rejected_as_validation_errors()
    {
        var vm = Build();
        vm.BaudRateText = "abc";
        vm.RetriesText = "abc";

        Assert.Contains("baudRate", vm.ValidationText);
        Assert.Contains("retries", vm.ValidationText);
    }

    [Fact]
    public void Build_options_maps_the_form_fields_onto_serial_connection_options()
    {
        var vm = Build();
        vm.PortName = "COM3";
        vm.BaudRateText = "19200";
        vm.DataBits = 7;
        vm.SelectedParity = Parity.Even;
        vm.SelectedStopBits = StopBits.Two;
        vm.SlaveIdText = "5";
        vm.TimeoutMsText = "250";
        vm.RetriesText = "2";

        var options = vm.BuildOptions();

        Assert.Equal("COM3", options.PortName);
        Assert.Equal(19200, options.BaudRate);
        Assert.Equal(7, options.DataBits);
        Assert.Equal(Parity.Even, options.Parity);
        Assert.Equal(StopBits.Two, options.StopBits);
        Assert.Equal(5, options.SlaveId);
        Assert.Equal(250, options.TimeoutMs);
        Assert.Equal(2, options.Retries);
        Assert.Empty(vm.BuildValidationErrors());
    }

    // --- Online → config modification must disconnect first (design §6.8) ----------------------------

    [Fact]
    public void Online_disables_config_editing_and_testing_until_disconnected()
    {
        var vm = Build();
        vm.ApplyConnectionState(ConnectionState.Online);

        Assert.True(vm.IsOnline);
        Assert.False(vm.IsFormEnabled);
        Assert.Contains("必须先断开", vm.OnlineEditHintText);
        Assert.False(vm.TestConnectionCommand.CanExecute(null));
    }

    [Fact]
    public void Connecting_and_reconnecting_are_not_online_so_config_stays_editable()
    {
        foreach (var state in new[] { ConnectionState.Connecting, ConnectionState.Reconnecting, ConnectionState.HeartbeatLost })
        {
            var vm = Build();
            vm.ApplyConnectionState(state);

            // Only ConnectionState.Online blocks the form (design §6.8: 在线时修改配置必须先断开); every other
            // link state leaves the config editable because a connect that is not yet Online is not a live session.
            Assert.False(vm.IsOnline);
            Assert.True(vm.IsFormEnabled);
        }
    }

    [Fact]
    public void Disconnected_allows_config_editing_and_testing()
    {
        var vm = Build();
        vm.ApplyConnectionState(ConnectionState.Disconnected);

        Assert.True(vm.IsFormEnabled);
        Assert.Equal(string.Empty, vm.OnlineEditHintText);
        Assert.True(vm.TestConnectionCommand.CanExecute(null));
    }

    // --- Online → disconnect affordance (design §6.8) ----------------------------------------------

    [Fact]
    public async Task Disconnect_while_online_calls_the_supervised_connection_and_is_guarded()
    {
        var supervisor = new FakeSupervisedConnection();
        var vm = new ConnectionSettingsViewModel(new FakeConnectionTester(), supervisor);
        vm.ApplyConnectionState(ConnectionState.Online);

        // While online the disconnect affordance is available; it is NOT available offline.
        Assert.True(vm.DisconnectCommand.CanExecute(null));
        Assert.True(vm.CanDisconnect);

        await vm.DisconnectCommand.ExecuteAsync(null);

        Assert.Single(supervisor.Disconnected);
        Assert.False(vm.CanDisconnect);
    }

    [Fact]
    public async Task Disconnect_is_unavailable_when_not_online()
    {
        var supervisor = new FakeSupervisedConnection();
        var vm = new ConnectionSettingsViewModel(new FakeConnectionTester(), supervisor);
        vm.ApplyConnectionState(ConnectionState.Disconnected);

        Assert.False(vm.DisconnectCommand.CanExecute(null));
        Assert.False(vm.CanDisconnect);

        await vm.DisconnectCommand.ExecuteAsync(null);

        // Not online → the disconnect never reached the supervised connection.
        Assert.Empty(supervisor.Disconnected);
    }

    [Fact]
    public async Task Disconnect_while_online_re_enables_the_form_after_the_state_event()
    {
        var supervisor = new FakeSupervisedConnection();
        var vm = new ConnectionSettingsViewModel(new FakeConnectionTester(), supervisor);
        vm.ApplyConnectionState(ConnectionState.Online);
        Assert.False(vm.IsFormEnabled);

        await vm.DisconnectCommand.ExecuteAsync(null);

        // The disconnect succeeded, so the supervisor reported Disconnected: the form unlocks again.
        Assert.True(vm.IsFormEnabled);
        Assert.True(vm.TestConnectionCommand.CanExecute(null));
        Assert.DoesNotContain("必须先断开", vm.OnlineEditHintText);
    }

    [Fact]
    public void Disconnect_command_is_disabled_while_a_test_is_in_flight()
    {
        var supervisor = new FakeSupervisedConnection();
        var vm = new ConnectionSettingsViewModel(new FakeConnectionTester(holdUntilCancelled: true), supervisor);
        vm.ApplyConnectionState(ConnectionState.Online);

        Assert.True(vm.DisconnectCommand.CanExecute(null));

        // While a test is in flight the disconnect affordance is disabled (CanDisconnect derives from IsTesting).
        vm.IsTesting = true;
        Assert.False(vm.DisconnectCommand.CanExecute(null));
    }

    // --- Parity / stop-bits Chinese labels (design §6.8, review fix) --------------------------------

    [Fact]
    public void Parity_and_stop_bits_options_are_exposed_with_chinese_labels()
    {
        var vm = Build();

        Assert.Equal(vm.ParityOptions.Count, vm.ParityOptionsText.Count);
        Assert.Equal(vm.StopBitsOptions.Count, vm.StopBitsOptionsText.Count);

        Assert.Equal("无", vm.ParityOptionsText[IndexOf(vm.ParityOptions, Parity.None)]);
        Assert.Equal("奇校验", vm.ParityOptionsText[IndexOf(vm.ParityOptions, Parity.Odd)]);
        Assert.Equal("偶校验", vm.ParityOptionsText[IndexOf(vm.ParityOptions, Parity.Even)]);
        Assert.Equal("标志", vm.ParityOptionsText[IndexOf(vm.ParityOptions, Parity.Mark)]);
        Assert.Equal("空格", vm.ParityOptionsText[IndexOf(vm.ParityOptions, Parity.Space)]);

        Assert.Equal("1", vm.StopBitsOptionsText[IndexOf(vm.StopBitsOptions, StopBits.One)]);
        Assert.Equal("2", vm.StopBitsOptionsText[IndexOf(vm.StopBitsOptions, StopBits.Two)]);
        Assert.Equal("1.5", vm.StopBitsOptionsText[IndexOf(vm.StopBitsOptions, StopBits.OnePointFive)]);
    }

    [Fact]
    public void Chinese_labels_are_stable_when_the_selection_changes()
    {
        var vm = Build();

        // The displayed label is derived, not a mutable array, so it never drifts from the selected enum.
        vm.SelectedParity = Parity.Even;
        Assert.Equal("偶校验", vm.ParityText(vm.SelectedParity));
        vm.SelectedStopBits = StopBits.Two;
        Assert.Equal("2", vm.StopBitsText(vm.SelectedStopBits));
    }

    // --- Connection test + cancellation (design §6.8) ----------------------------------------------

    [Fact]
    public async Task Connection_test_succeeds_with_a_valid_configuration()
    {
        var tester = new FakeConnectionTester();
        var vm = new ConnectionSettingsViewModel(tester, new FakeSupervisedConnection());
        vm.ApplyConnectionState(ConnectionState.Disconnected);

        await vm.TestConnectionCommand.ExecuteAsync(null);

        Assert.Single(tester.Tested);
        Assert.Contains("成功", vm.TestResultText);
        Assert.False(vm.IsTesting);
    }

    [Fact]
    public async Task Test_connection_is_not_attempted_with_an_invalid_configuration()
    {
        var tester = new FakeConnectionTester();
        var vm = new ConnectionSettingsViewModel(tester, new FakeSupervisedConnection());
        vm.ApplyConnectionState(ConnectionState.Disconnected);
        vm.BaudRateText = "-5";

        await vm.TestConnectionCommand.ExecuteAsync(null);

        Assert.Empty(tester.Tested);
        Assert.Contains("配置无效", vm.TestResultText);
    }

    [Fact]
    public async Task A_failed_test_reports_the_transport_reason()
    {
        var tester = new FakeConnectionTester(throwOnTest: true);
        var vm = new ConnectionSettingsViewModel(tester, new FakeSupervisedConnection());
        vm.ApplyConnectionState(ConnectionState.Disconnected);

        await vm.TestConnectionCommand.ExecuteAsync(null);

        Assert.Contains("失败", vm.TestResultText);
        Assert.False(vm.IsTesting);
    }

    [Fact]
    public async Task Connection_test_can_be_cancelled()
    {
        var tester = new FakeConnectionTester(holdUntilCancelled: true);
        var vm = new ConnectionSettingsViewModel(tester, new FakeSupervisedConnection());
        vm.ApplyConnectionState(ConnectionState.Disconnected);

        var inFlight = vm.TestConnectionCommand.ExecuteAsync(null);
        Assert.True(vm.TestConnectionCommand.IsRunning);
        Assert.True(vm.IsTesting);

        vm.TestConnectionCommand.Cancel();
        await inFlight;

        Assert.False(vm.TestConnectionCommand.IsRunning);
        Assert.False(vm.IsTesting);
        Assert.Contains("取消", vm.TestResultText);
    }

    // --- Helpers + fakes -----------------------------------------------------------------------------

    private static ConnectionSettingsViewModel Build(SerialConnectionOptions? initial = null)
        => new(new FakeConnectionTester(), new FakeSupervisedConnection(), initial);

    private static int IndexOf<T>(IReadOnlyList<T> source, T value)
    {
        for (var i = 0; i < source.Count; i++)
        {
            if (EqualityComparer<T>.Default.Equals(source[i], value))
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>Read-only <see cref="IConnectionTester"/> fake. The default completes immediately with the
    /// options it was given; <paramref name="holdUntilCancelled"/> makes the test wait until the passed token
    /// is cancelled; <paramref name="throwOnTest"/> simulates a transport failure.</summary>
    private sealed class FakeConnectionTester : IConnectionTester
    {
        private readonly bool _holdUntilCancelled;
        private readonly bool _throwOnTest;

        public FakeConnectionTester(bool holdUntilCancelled = false, bool throwOnTest = false)
        {
            _holdUntilCancelled = holdUntilCancelled;
            _throwOnTest = throwOnTest;
        }

        public List<SerialConnectionOptions> Tested { get; } = new();

        public async Task TestAsync(SerialConnectionOptions options, CancellationToken cancellationToken)
        {
            Tested.Add(options);

            if (_throwOnTest)
            {
                throw new IOException("simulated COM port not found");
            }

            if (!_holdUntilCancelled)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return;
            }

            var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var registration = cancellationToken.Register(() => gate.TrySetCanceled(cancellationToken));
            await gate.Task;
        }
    }

    /// <summary>Fake <see cref="ISupervisedConnection"/> recording disconnect calls, for the disconnect
    /// affordance tests. Only <see cref="DisconnectAsync"/> is exercised here.</summary>
    private sealed class FakeSupervisedConnection : ISupervisedConnection
    {
        public List<CancellationToken> Disconnected { get; } = new();

        public Task ConnectAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task DisconnectAsync(CancellationToken cancellationToken)
        {
            Disconnected.Add(cancellationToken);
            return Task.CompletedTask;
        }

        public Task<bool> ProbeAsync(CancellationToken cancellationToken) => Task.FromResult(true);
    }
}
