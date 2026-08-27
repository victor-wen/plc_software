using System.Threading;

namespace PlcSoftware.App.Services;

/// <summary>
/// Ensures only one app instance is running. The first instance to acquire the guard owns the named mutex and may
/// start the communication service; a second instance finds the mutex already held and fails fast (it cannot start
/// the communication service because the first instance owns the underlying device).
///
/// <para><b>Windows-only.</b> The mutex is created under the <c>Global\</c> namespace (falling back to <c>Local\</c>)
/// using a caller-supplied name, so it is visible across sessions for the same user and testable by injecting the
/// name. The guard is <see cref="IDisposable"/>: <see cref="Release"/> (and Dispose) releases and disposes the mutex
/// so a later instance can acquire it.</para>
/// </summary>
public sealed class SingleInstanceGuard : IDisposable
{
    private readonly Mutex _mutex;
    private bool _acquired;

    /// <summary>
    /// Creates a guard for the given mutex <paramref name="name"/>. The name is used verbatim to build the named
    /// mutex; the caller may pass a bare name (guarded as <c>Local\</c>) or a fully qualified <c>Global\</c> name.
    /// </summary>
    public SingleInstanceGuard(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("The mutex name must not be null or whitespace.", nameof(name));
        }

        _mutex = new Mutex(initiallyOwned: false, name);
    }

    /// <summary>Whether this guard currently owns the single-instance mutex.</summary>
    public bool IsAcquired => _acquired;

    /// <summary>
    /// Attempts to acquire the single-instance mutex. Returns <c>true</c> if this guard now owns it (the first, or
    /// the newly acquired, instance) and <c>false</c> if another instance already holds it. The acquisition is
    /// non-blocking: if the mutex is contended, <c>false</c> is returned immediately so the second instance fails
    /// fast rather than waiting.
    /// </summary>
    public bool TryAcquire()
    {
        if (_acquired)
        {
            return true;
        }

        try
        {
            _acquired = _mutex.WaitOne(TimeSpan.Zero);
        }
        catch (AbandonedMutexException)
        {
            // A previous owner exited without releasing (e.g. the process was killed). The mutex is ours now.
            _acquired = true;
        }

        return _acquired;
    }

    /// <summary>
    /// Releases and disposes the mutex so the next instance may acquire it. Safe to call multiple times; a guard that
    /// never acquired does nothing beyond disposing the underlying mutex handle.
    /// </summary>
    public void Release()
    {
        if (_acquired)
        {
            try
            {
                _mutex.ReleaseMutex();
            }
            finally
            {
                _acquired = false;
            }
        }
    }

    public void Dispose()
    {
        Release();
        _mutex.Dispose();
    }
}
