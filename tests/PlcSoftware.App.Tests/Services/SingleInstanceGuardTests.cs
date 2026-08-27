using System.IO;
using PlcSoftware.App.Services;

namespace PlcSoftware.App.Tests.Services;

/// <summary>
/// Pins the single-instance behaviour of <see cref="SingleInstanceGuard"/>: the first guard to acquire the named
/// mutex owns it, a second guard for the same name fails fast, and releasing the first lets a later guard acquire.
///
/// <para><b>Compile-only on WSL.</b> The guard is Windows-only (<c>Global\</c>/<c>Local\</c> named mutex), so these
/// tests run on the Windows CI runner like the rest of the App suite; on the WSL cross-build the project still
/// compiles via <c>EnableWindowsTargeting</c>. The CrashReporter checks use a temp directory and would also run on
/// Linux had the App suite not been Windows-targeted.</para>
/// </summary>
public class SingleInstanceGuardTests
{
    private static string MutexName() => $"PlcSoftware.SingleInstanceGuard.Tests.{Guid.NewGuid():N}";

    [Fact]
    public void Acquire_then_release_then_acquire_again()
    {
        var name = MutexName();
        using (var first = new SingleInstanceGuard(name))
        {
            Assert.True(first.TryAcquire());
            Assert.True(first.IsAcquired);

            first.Release();
            Assert.False(first.IsAcquired);

            Assert.True(first.TryAcquire());
            Assert.True(first.IsAcquired);
        }
    }

    [Fact]
    public void Second_try_acquire_fails_while_first_is_held()
    {
        var name = MutexName();
        using var first = new SingleInstanceGuard(name);
        using var second = new SingleInstanceGuard(name);

        Assert.True(first.TryAcquire());

        // A named Mutex is re-entrant on the SAME thread, so simulating a second instance requires a
        // different thread (a different process in production). Acquire the second guard from a worker
        // thread that blocks while the first instance holds the mutex; the result must be false.
        var secondResult = false;
        var thread = new Thread(() => secondResult = second.TryAcquire());
        thread.Start();
        thread.Join();

        Assert.False(secondResult);
        Assert.False(second.IsAcquired);
    }

    [Fact]
    public void Second_try_acquire_succeeds_after_first_releases()
    {
        var name = MutexName();
        using var first = new SingleInstanceGuard(name);
        using var second = new SingleInstanceGuard(name);

        Assert.True(first.TryAcquire());
        first.Release();

        var thread = new Thread(() => Assert.True(second.TryAcquire()));
        thread.Start();
        thread.Join();

        Assert.True(second.IsAcquired);
    }

    [Fact]
    public void Releasing_without_acquiring_is_a_no_op()
    {
        var guard = new SingleInstanceGuard(MutexName());
        Assert.Throws<ArgumentException>(() => new SingleInstanceGuard("  "));
        guard.Release();
        guard.Dispose();
    }

    [Fact]
    public void Reacquiring_while_still_acquired_returns_true()
    {
        using var guard = new SingleInstanceGuard(MutexName());
        Assert.True(guard.TryAcquire());
        Assert.True(guard.TryAcquire());
    }
}

/// <summary>
/// Pins the record-only crash logging of <see cref="CrashReporter"/>: <see cref="CrashReporter.Record"/> writes a
/// file containing the exception details and never throws — even when the directory cannot be written.
/// </summary>
public class CrashReporterTests
{
    [Fact]
    public void Record_writes_a_file_with_exception_details()
    {
        var dir = Path.Combine(Path.GetTempPath(), "plc-crash-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var ex = new InvalidOperationException("boom");
            var path = CrashReporter.Record(DateTime.Now, ex, dir);

            Assert.NotNull(path);
            Assert.True(File.Exists(path));
            var content = File.ReadAllText(path);

            Assert.Contains("boom", content);
            Assert.Contains(nameof(InvalidOperationException), content);
            Assert.Contains("Stack:", content);
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }

    [Fact]
    public void Record_does_not_throw_when_the_directory_is_invalid()
    {
        var badPath = Path.Combine(Path.GetTempPath(), "plc-crash-tests", "\0");
        var path = CrashReporter.Record(DateTime.Now, new Exception("x"), badPath);
        Assert.Null(path);
    }
}
