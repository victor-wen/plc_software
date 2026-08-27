using PlcSoftware.App.Services;
using PlcSoftware.Core.Configuration;

namespace PlcSoftware.App.Tests.Services;

/// <summary>
/// Pins the bounded-timeout behaviour of <see cref="SerialConnectionTester"/> (review fix): a probe that is
/// cancelled by the <em>operator</em> stays an <see cref="OperationCanceledException"/> (连接测试已取消), while a
/// probe that outlives the wall-clock bound is cancelled by the <em>timer</em> and surfaces as
/// <see cref="TimeoutException"/> (连接测试超时) — never an unhandled exception.
///
/// <para>The <c>openPort</c> seam lets a test simulate a hanging <see cref="System.IO.Ports.SerialPort.Open"/>
/// by returning a delegate that never observes its token (exactly the platform limitation the timeout guards
/// against). These tests are compile-only on the WSL cross-build (WindowsDesktop runtime absent) and run on the
/// Windows CI runner like the rest of the App suite.</para>
/// </summary>
public class SerialConnectionTesterTests
{
    private static readonly SerialConnectionOptions Options = new();

    [Fact]
    public async Task Hanging_open_outlives_the_bound_and_reports_a_timeout()
    {
        // A probe that never returns and never observes its token, mimicking a SerialPort.Open wedged on a
        // dead device. The bound must still surface a TimeoutException, not a hang or an OCE.
        Func<SerialConnectionOptions, IDisposable> hanging = _ =>
        {
            using var never = new ManualResetEventSlim(false);
            never.Wait();
            return null!;
        };

        var tester = new SerialConnectionTester(TimeSpan.FromMilliseconds(100), hanging);

        var ex = await Assert.ThrowsAsync<TimeoutException>(
            () => tester.TestAsync(Options, CancellationToken.None));

        Assert.Equal(SerialConnectionTester.TimeoutMessage, ex.Message);
    }

    [Fact]
    public async Task Operator_cancellation_stays_an_operation_canceled_exception()
    {
        // A probe that never returns but DOES observe the operator token: cancel must stay an OCE.
        Func<SerialConnectionOptions, IDisposable> cancellable = _ =>
        {
            using var gate = new ManualResetEventSlim(false);
            gate.Wait();
            return null!;
        };

        using var token = new CancellationTokenSource();
        var tester = new SerialConnectionTester(TimeSpan.FromSeconds(10), cancellable);

        var inFlight = tester.TestAsync(Options, token.Token);
        token.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => inFlight);
    }

    [Fact]
    public async Task A_healthy_probe_completes_and_disposes_the_port()
    {
        var disposed = false;
        Func<SerialConnectionOptions, IDisposable> opening = _ => new TrackingResource(() => disposed = true);

        var tester = new SerialConnectionTester(TimeSpan.FromSeconds(10), opening);

        await tester.TestAsync(Options, CancellationToken.None);

        Assert.True(disposed, "the opened port resource must be disposed after the probe.");
    }

    [Fact]
    public void A_non_positive_timeout_is_rejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new SerialConnectionTester(TimeSpan.Zero));
    }

    private sealed class TrackingResource : IDisposable
    {
        private readonly Action _onDispose;

        public TrackingResource(Action onDispose) => _onDispose = onDispose;

        public void Dispose() => _onDispose();
    }
}
