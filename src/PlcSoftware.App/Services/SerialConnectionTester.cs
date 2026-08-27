using System.IO.Ports;
using NModbus.IO;
using PlcSoftware.Core.Configuration;
using PlcSoftware.Infrastructure.Modbus;

namespace PlcSoftware.App.Services;

/// <summary>
/// Runs a single connection test against a set of <see cref="SerialConnectionOptions"/>. This is the seam the
/// <c>ConnectionSettingsViewModel</c> talks to: the view model exposes the user's edited config and cancels the
/// test through the token, never touching an NModbus session or a real port directly.
///
/// <para><b>Scope.</b> The test is deliberately behind an explicit user action (design §6.8 支持连接测试) and the
/// production implementation probes the configured serial port through the <c>SerialPortFactory</c> seam — it
/// does <em>not</em> wire the production polling/transport path (the demo ships on the in-memory simulation). It
/// only reports whether the port can be opened with the edited parameters, so a bad baud/parity/stop-bits
/// combination (or an absent device) surfaces as a test failure without ever starting the transport.</para>
/// </summary>
public interface IConnectionTester
{
    /// <summary>
    /// Tests the connection for <paramref name="options"/>, honouring <paramref name="cancellationToken"/>.
    /// The implementation must not run indefinitely: the probe is bounded by an implementation timeout, so an
    /// unresponsive port surfaces as a bounded failure rather than a hang.
    /// </summary>
    Task TestAsync(SerialConnectionOptions options, CancellationToken cancellationToken);
}

/// <summary>
/// Production <see cref="IConnectionTester"/> that opens the configured serial port through
/// <see cref="SerialPortFactory"/>. Opening the port validates the configured baud/data/parity/stop-bits and
/// that a device is present at that port; the resource is disposed immediately after the probe. The call is
/// pushed onto a worker so a blocking <see cref="SerialPort"/> open does not stall the UI.
///
/// <para><b>Bounded timeout (review fix).</b> The probe is bounded by a configurable wall-clock bound
/// (<see cref="Timeout"/>, default 10 s) via <c>Task.WaitAsync</c>, which bounds the <em>whole wait</em>, not just
/// a boundary check. A hanging <see cref="SerialPort.Open"/> that never observes its token therefore still cannot
/// wedge the page: the bound elapses and the test surfaces as <see cref="TimeoutException"/> (连接测试超时),
/// never an unhandled exception. An operator-initiated cancel still surfaces as
/// <see cref="OperationCanceledException"/> (连接测试已取消). The wedged worker may linger in the background
/// (a platform limitation — a blocked <c>SerialPort.Open</c> cannot be interrupted) but the UI and the caller
/// are released by the bound.</para>
/// </summary>
public sealed class SerialConnectionTester : IConnectionTester
{
    /// <summary>Default wall-clock bound for a single probe (10 s).</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(10);

    private readonly TimeSpan _timeout;
    private readonly Func<SerialConnectionOptions, IDisposable> _openPort;

    public SerialConnectionTester(TimeSpan? timeout = null, Func<SerialConnectionOptions, IDisposable>? openPort = null)
    {
        _timeout = timeout ?? DefaultTimeout;
        if (_timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout), _timeout, "must be positive.");
        }

        _openPort = openPort ?? (options => new DisposableStreamResource(new SerialPortFactory(options).Create()));
    }

    /// <summary>
    /// The wall-clock bound applied to each probe. Cancellation from the operator (the passed token) is mapped
    /// to <see cref="OperationCanceledException"/>; only this bound's expiry is mapped to
    /// <see cref="TimeoutException"/>. Both are observed by the caller.
    /// </summary>
    public TimeSpan Timeout => _timeout;

    public async Task TestAsync(SerialConnectionOptions options, CancellationToken cancellationToken)
    {
        if (options is null)
        {
            throw new ArgumentNullException(nameof(options));
        }

        cancellationToken.ThrowIfCancellationRequested();

        // The probe runs on a worker so a blocking SerialPort open does not stall the UI. The whole wait is
        // bounded by WaitAsync: whoever fires first (the probe, the operator token, or the wall-clock bound)
        // resolves it, and each of the three surfaces the caller's expected exception.
        var probe = Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();

            // A port closed on completion; a bad config / absent device throws here and is reported as a test
            // failure on the page.
            using var resource = _openPort(options);
        }, cancellationToken);

        await probe.WaitAsync(_timeout, cancellationToken);
    }

    /// <summary>Wraps an <see cref="IStreamResource"/> as a plain <see cref="IDisposable"/> so the default
    /// <see cref="SerialPortFactory"/> (which returns an <see cref="IStreamResource"/>) fits the probe seam.</summary>
    private sealed class DisposableStreamResource : IDisposable
    {
        private readonly IStreamResource _resource;

        public DisposableStreamResource(IStreamResource resource) => _resource = resource;

        public void Dispose() => _resource.Dispose();
    }
}
