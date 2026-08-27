using System.IO;
using System.Text;
using System.Threading.Tasks;

namespace PlcSoftware.App.Services;

/// <summary>
/// Records diagnostic information when the process is about to crash. On an unhandled exception (either an
/// <see cref="AppDomain.UnhandledException"/> or an unobserved
/// <see cref="TaskScheduler.UnobservedTaskException"/>) it writes a timestamp, the exception message and the stack
/// trace to a log file under the configured log directory. It deliberately does <em>not</em> restore or execute any
/// commands (设计: 异常退出记录诊断信息但不恢复命令) — it is a record-only hook, so it never blocks or alters the
/// process's fatal path.
///
/// <para><b>Platform agnostic.</b> The class stays WPF-free and Core-free on purpose: no WPF types (e.g.
/// <c>DispatcherUnhandledException</c>) and no domain types appear here, so it compiles against any target framework
/// and can be attached regardless of the UI stack. Every write is guarded so no logging failure can ever throw out
/// of an exception-handling path.</para>
/// </summary>
public static class CrashReporter
{
    private static int _attached;

    /// <summary>
    /// Hooks the process's unhandled-exception events to <see cref="Record(DateTime, Exception, string)"/>.
    /// Safe to call multiple times: only the first call wires the handlers. The <paramref name="logDir"/> is used
    /// verbatim as the base directory for crash logs (created on demand by <see cref="Record"/>).
    /// </summary>
    public static void Attach(string logDir)
    {
        if (Interlocked.Exchange(ref _attached, 1) != 0)
        {
            return;
        }

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Record(DateTime.Now, e.ExceptionObject as Exception, logDir);
        TaskScheduler.UnobservedTaskException += (_, e) =>
            Record(DateTime.Now, e.Exception, logDir);
    }

    /// <summary>
    /// Writes a crash log for <paramref name="ex"/> under <paramref name="logDir"/>. The file is named
    /// <c>crash-yyyyMMdd-HHmmssff.log</c> and contains the timestamp, the exception type, the message and the stack
    /// trace. Returns the full path written, or <c>null</c> if the write could not be completed.
    ///
    /// <para><b>Never throws.</b> This is called from exception-handling paths, so any <see cref="Exception"/> from
    /// directory creation, file write or formatting is swallowed and reported as <c>null</c> instead of being
    /// rethrown into the failing path.</para>
    /// </summary>
    public static string? Record(DateTime timestamp, Exception? ex, string logDir)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(logDir))
            {
                return null;
            }

            Directory.CreateDirectory(logDir);

            var fileName = $"crash-{timestamp:yyyyMMdd-HHmmssff}.log";
            var path = Path.Combine(logDir, fileName);

            var lines = new StringBuilder();
            lines.AppendLine($"Timestamp: {timestamp:O}");
            lines.AppendLine($"Exception: {(ex?.GetType().FullName ?? "(none)")}");
            lines.AppendLine($"Message:   {ex?.Message}");
            lines.AppendLine("Stack:");
            lines.AppendLine(ex?.ToString() ?? "(no stack)");

            File.WriteAllText(path, lines.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            return path;
        }
        catch
        {
            return null;
        }
    }
}
