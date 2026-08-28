using System.IO;
using PlcSoftware.App.ViewModels;

namespace PlcSoftware.App.Tests.ViewModels;

/// <summary>
/// Pins how <see cref="HistoryViewModel"/> drives the history page (design §6.5): the query loads the
/// alarm and audit rows for the selected date window, a query failure is reported on the status text
/// (never thrown), the export flattens both row sets into a formula-injection-safe CSV document, and an
/// export failure (including a throwing <c>SaveFile</c>) is reported on the status text without crashing
/// the VM.
///
/// <para><b>No WPF dependency.</b> The view model consumes rows through injected <c>Func</c>s and the
/// export through an injected <c>SaveFile</c> action, so the suite is WPF-runtime-free: it CANNOT run on
/// the WSL/Linux cross-build (WindowsDesktop runtime absent) — on Linux it only contributes a compile
/// RED/GREEN check; full execution (GREEN) happens on the Windows CI runner.</para>
/// </summary>
public class HistoryViewModelTests
{
    private static HistoryRow Alarm(DateTime at, string description, string? value = null)
        => new(at, "报警", description, value);

    private static HistoryRow Audit(DateTime at, string description, string? value = null)
        => new(at, "操作", description, value);

    /// <summary>Builds a VM whose query delegates are backed by captured mutable lists and a recording save file.</summary>
    private static (HistoryViewModel Vm, List<HistoryRow> Alarms, List<HistoryRow> Audits, List<(string FileName, string Content)> Saved)
        Build()
    {
        var alarms = new List<HistoryRow>();
        var audits = new List<HistoryRow>();
        var saved = new List<(string, string)>();
        var vm = new HistoryViewModel(
            (f, t) => alarms,
            (f, t) => audits,
            (fileName, content) => saved.Add((fileName, content)));
        return (vm, alarms, audits, saved);
    }

    [Fact]
    public void Query_loads_rows_and_reports_counts()
    {
        var (vm, alarms, audits, _) = Build();
        alarms.Add(Alarm(new DateTime(2026, 1, 10), "光栅屏蔽"));
        audits.Add(Audit(new DateTime(2026, 1, 11), "写入 D126"));

        vm.QueryCommand.Execute(null);

        Assert.Single(vm.AlarmRows);
        Assert.Single(vm.AuditRows);
        Assert.Contains("报警 1 条，操作 1 条", vm.StatusText);
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public void Query_passes_the_date_window_to_the_fetch()
    {
        DateTime? capturedFrom = null, capturedTo = null;
        var vm = new HistoryViewModel(
            (f, t) =>
            {
                capturedFrom = f;
                capturedTo = t;
                return new List<HistoryRow>();
            },
            (f, t) => new List<HistoryRow>());

        var from = new DateTime(2026, 1, 1);
        var to = new DateTime(2026, 1, 31);
        vm.DateFrom = from;
        vm.DateTo = to;

        vm.QueryCommand.Execute(null);

        Assert.Equal(from, capturedFrom);
        Assert.Equal(to, capturedTo);
    }

    [Fact]
    public void Query_failure_sets_message_and_does_not_throw()
    {
        var vm = new HistoryViewModel((f, t) => throw new InvalidOperationException("db locked"),
            (f, t) => new List<HistoryRow>());

        vm.QueryCommand.Execute(null);

        Assert.StartsWith("查询失败：", vm.StatusText);
        Assert.Contains("db locked", vm.StatusText);
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public void Query_failure_keeps_previously_loaded_rows()
    {
        var calls = 0;
        var vm = new HistoryViewModel(
            (f, t) => calls++ == 0
                ? new List<HistoryRow> { Alarm(new DateTime(2026, 1, 10), "光栅屏蔽") }
                : throw new InvalidOperationException("db locked"),
            (f, t) => new List<HistoryRow>());

        vm.QueryCommand.Execute(null);
        Assert.Single(vm.AlarmRows);

        vm.QueryCommand.Execute(null); // second query fails.

        Assert.StartsWith("查询失败：", vm.StatusText);
        Assert.Single(vm.AlarmRows); // the previously loaded row survives the failed refresh.
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public void Export_produces_escaped_csv()
    {
        var (vm, alarms, audits, saved) = Build();
        alarms.Add(Alarm(new DateTime(2026, 1, 10), "急停,屏蔽", "=SUM(A1:A2)"));
        audits.Add(Audit(new DateTime(2026, 1, 11), "写入 D126", "300"));

        // The CSV export flattens AlarmRows/AuditRows (HistoryViewModel.BuildCsv), NOT the injected query
        // delegates. Those collections are populated by Query(), so load them first — mirroring
        // Query_loads_rows_and_reports_counts. Without this, Export emits only the header row.
        vm.QueryCommand.Execute(null);

        vm.ExportCommand.Execute(null);

        var (fileName, content) = Assert.Single(saved);
        Assert.Equal("history_", fileName.Substring(0, 8));
        Assert.Contains("时间,类型,描述,数值", content);
        Assert.Contains("\"急停,屏蔽\"", content);   // quoted comma field.
        Assert.Contains("'=SUM(A1:A2)", content);    // formula-prefix neutralised.
        Assert.Contains("写入 D126", content);       // Chinese audit description.
        Assert.Contains("300", content);
        Assert.Contains("导出完成", vm.StatusText);
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public void Export_failure_sets_message_and_does_not_throw()
    {
        var vm = new HistoryViewModel((f, t) => new List<HistoryRow>(),
            (f, t) => new List<HistoryRow>(),
            (fileName, content) => throw new IOException("disk full"));

        vm.ExportCommand.Execute(null);

        Assert.StartsWith("导出失败：", vm.StatusText);
        Assert.Contains("disk full", vm.StatusText);
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public void Export_with_no_save_file_still_succeeds()
    {
        var vm = new HistoryViewModel((f, t) => new List<HistoryRow>(),
            (f, t) => new List<HistoryRow>());

        vm.ExportCommand.Execute(null);

        Assert.Contains("导出完成", vm.StatusText);
        Assert.False(vm.IsBusy);
    }
}
