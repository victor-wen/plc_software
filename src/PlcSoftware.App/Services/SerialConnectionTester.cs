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
    /// <summary>Tests the connection for <paramref name="options"/>, honouring <paramref name="cancellationToken"/>.</summary>
    Task TestAsync(SerialConnectionOptions options, CancellationToken cancellationToken);
}

/// <summary>
/// Production <see cref="IConnectionTester"/> that opens the configured serial port through
/// <see cref="SerialPortFactory"/>. Opening the port validates the configured baud/data/parity/stop-bits and
/// that a device is present at that port; the resource is disposed immediately after the probe. The call is
/// pushed onto a worker so a blocking <see cref="SerialPort"/> open does not stall the UI, and it observes the
/// cancellation token at the boundary.
/// </summary>
public sealed class SerialConnectionTester : IConnectionTester
{
    public Task TestAsync(SerialConnectionOptions options, CancellationToken cancellationToken)
    {
        if (options is null)
        {
            throw new ArgumentNullException(nameof(options));
        }

        cancellationToken.ThrowIfCancellationRequested();

        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();

            // A port closed on completion; a bad config / absent device throws here and is reported as a test
            // failure on the page.
            var factory = new SerialPortFactory(options);
            using var resource = factory.Create();
        }, cancellationToken);
    }
}
