using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PlcSoftware.Infrastructure.Persistence;

namespace PlcSoftware.App.ViewModels;

/// <summary>
/// One flat history record shown on the history page (design §6.5): a timestamp, a source kind
/// (报警 / 操作), the human-readable description and an optional value/quantity. Alarm and audit rows have
/// different shapes, so both are normalised onto this single display row that the CSV export flattens.
/// </summary>
public sealed record HistoryRow(DateTime Timestamp, string Kind, string Description, string? Value);

/// <summary>
/// The history page (design §6.5): retains, queries and CSV-exports the alarm (K1-K7) and audit
/// (host-write) histories between an optional date range.
///
/// <para><b>Dependencies are injected, WPF-free.</b> Rows are fetched through two injected
/// <c>Func</c>s (<see cref="QueryAlarms"/> / <see cref="QueryAudits"/>), so the VM is wired to the SQLite
/// repositories at the composition root and stays testable under a pure unit test host. The CSV text is
/// produced through the static <see cref="CsvExporter"/> (formula-injection safe, RFC-4180 quoting) and
/// handed to an injected <c>SaveFile</c> action so the UI's save dialog never leaks into the VM.</para>
///
/// <para><b>No-throw contract.</b> Neither <see cref="QueryCommand"/> nor <see cref="ExportCommand"/>
/// lets an exception escape to a command (it would surface on the UI thread): a query/export failure is
/// reported on <see cref="StatusText"/> and the current rows are left intact. <see cref="IsBusy"/> guards
/// the page while a query/export is in flight.</para>
/// </summary>
public sealed partial class HistoryViewModel : ObservableObject
{
    private readonly Func<DateTime?, DateTime?, List<HistoryRow>> _queryAlarms;
    private readonly Func<DateTime?, DateTime?, List<HistoryRow>> _queryAudits;
    private readonly Action<string, string>? _saveFile;

    /// <summary>Start of the query window (inclusive); <c>null</c> = unbounded.</summary>
    [ObservableProperty]
    private DateTime? _dateFrom;

    /// <summary>End of the query window (inclusive); <c>null</c> = unbounded.</summary>
    [ObservableProperty]
    private DateTime? _dateTo;

    /// <summary>True while a query or export is in flight (guards the page against double fires).</summary>
    [ObservableProperty]
    private bool _isBusy;

    /// <summary>User-facing status / error text (loading outcome, export outcome, failure message).</summary>
    [ObservableProperty]
    private string _statusText = string.Empty;

    /// <summary>Alias of <see cref="StatusText"/>.</summary>
    public string Message => StatusText;

    /// <summary>The loaded K1-K7 alarm rows (newest first from the repository).</summary>
    public ObservableCollection<HistoryRow> AlarmRows { get; } = new();

    /// <summary>The loaded host-write audit rows (newest first from the repository).</summary>
    public ObservableCollection<HistoryRow> AuditRows { get; } = new();

    public HistoryViewModel(
        Func<DateTime?, DateTime?, List<HistoryRow>>? queryAlarms,
        Func<DateTime?, DateTime?, List<HistoryRow>>? queryAudits,
        Action<string, string>? saveFile = null)
    {
        _queryAlarms = queryAlarms ?? throw new ArgumentNullException(nameof(queryAlarms));
        _queryAudits = queryAudits ?? throw new ArgumentNullException(nameof(queryAudits));
        _saveFile = saveFile;
    }

    /// <summary>Loads the alarm and audit rows for the selected <see cref="DateFrom"/>/<see cref="DateTo"/>
    /// window. A query failure is reported on <see cref="StatusText"/> (never thrown), keeping the previously
    /// loaded rows visible.</summary>
    [RelayCommand]
    private void Query()
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        try
        {
            // Load into temp lists first so a mid-way failure never leaves the two collections
            // half-populated: the previously shown rows stay intact.
            var alarms = _queryAlarms(DateFrom, DateTo);
            var audits = _queryAudits(DateFrom, DateTo);

            Replace(AlarmRows, alarms);
            Replace(AuditRows, audits);

            StatusText = $"查询完成：报警 {AlarmRows.Count} 条，操作 {AuditRows.Count} 条。";
        }
        catch (Exception ex)
        {
            StatusText = $"查询失败：{ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Builds one CSV text (alarms then audits) via <see cref="CsvExporter"/> and hands it to the
    /// injected <c>SaveFile(fileName, content)</c> action. An export failure (including a throwing
    /// <c>SaveFile</c>) is reported on <see cref="StatusText"/>, never thrown.</summary>
    [RelayCommand]
    private void Export()
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        try
        {
            var fileName = $"history_{DateTime.Now:yyyyMMdd_HHmmss}.csv";
            var content = BuildCsv();
            _saveFile?.Invoke(fileName, content);
            StatusText = $"导出完成：{fileName}（报警 {AlarmRows.Count} 条，操作 {AuditRows.Count} 条）。";
        }
        catch (Exception ex)
        {
            StatusText = $"导出失败：{ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Flattens the loaded alarm and audit rows into a single, formula-injection-safe CSV
    /// document: a header row, then one row per alarm (kind 报警) and per audit (kind 操作).</summary>
    private string BuildCsv()
    {
        var builder = new StringBuilder();
        using var writer = new StringWriter(builder);
        CsvExporter.WriteRow(writer, new[] { "时间", "类型", "描述", "数值" });

        foreach (var row in AlarmRows)
        {
            CsvExporter.WriteRow(writer, CsvFields(row));
        }

        foreach (var row in AuditRows)
        {
            CsvExporter.WriteRow(writer, CsvFields(row));
        }

        return builder.ToString();
    }

    private static string[] CsvFields(HistoryRow row)
        => new[]
        {
            row.Timestamp.ToString("yyyy-MM-dd HH:mm:ss"),
            row.Kind,
            row.Description,
            row.Value ?? string.Empty,
        };

    private static void Replace(ObservableCollection<HistoryRow> target, List<HistoryRow> source)
    {
        target.Clear();
        foreach (var row in source)
        {
            target.Add(row);
        }
    }
}
