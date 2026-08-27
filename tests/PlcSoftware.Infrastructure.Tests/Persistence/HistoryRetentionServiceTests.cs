using PlcSoftware.Core.Abstractions;
using PlcSoftware.Core.Models;
using PlcSoftware.Infrastructure.Persistence;

namespace PlcSoftware.Infrastructure.Tests.Persistence;

/// <summary>
/// Behavioural tests for the history-retention service (design §6.5).
///
/// Verified rules:
///   - <see cref="HistoryRetentionService"/> deletes alarm, audit, production, comms and debug rows whose
///     timestamp is strictly older than the retention cut-off;
///   - rows exactly at the cut-off (the boundary) are retained;
///   - calling <see cref="HistoryRetentionService.Cleanup"/> on a closed database returns
///     <c>ok = false</c> without throwing.
/// </summary>
public class HistoryRetentionServiceTests : IDisposable
{
    private readonly string _dbPath;
    private readonly SqliteDatabase _db;

    public HistoryRetentionServiceTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"plc-retention-{Guid.NewGuid():N}.db");
        _db = new SqliteDatabase(_dbPath);
        _db.EnsureSchema();
    }

    public void Dispose()
    {
        _db.Dispose();
        DeleteDatabaseFiles();
    }

    private void DeleteDatabaseFiles()
    {
        if (File.Exists(_dbPath))
        {
            File.Delete(_dbPath);
        }

        if (File.Exists(_dbPath + "-wal"))
        {
            File.Delete(_dbPath + "-wal");
        }

        if (File.Exists(_dbPath + "-shm"))
        {
            File.Delete(_dbPath + "-shm");
        }
    }

    private void SeedOldAndNewRecords()
    {
        // Retention is 380 days; "now" drifts as the test runs but the two timestamps are ~1 year apart,
        // which is safely inside / outside any plausible 380-day window anchored at the current time.
        var old = DateTime.UtcNow.AddYears(-2);
        var fresh = DateTime.UtcNow.AddDays(-1);

        var alarms = new AlarmRepository(_db);
        alarms.InsertStarted(new FaultDefinition { Code = 1, Message = "old" }, old);
        alarms.InsertStarted(new FaultDefinition { Code = 2, Message = "fresh" }, fresh);

        var audits = new AuditRepository(_db);
        audits.Record(new AuditEvent(AuditCategory.Parameter, "old", 1, "old"), old);
        audits.Record(new AuditEvent(AuditCategory.Parameter, "fresh", 2, "fresh"), fresh);

        var production = new ProductionRepository(_db);
        production.AppendProduction(1, old);
        production.AppendProduction(2, fresh);
        production.AppendComms("old", null, old);
        production.AppendComms("fresh", null, fresh);
        production.AppendDebugCommand("old", 0, null, old);
        production.AppendDebugCommand("fresh", 1, null, fresh);
    }

    [Fact]
    public void Cleanup_DeletesRowsOlderThanRetention()
    {
        SeedOldAndNewRecords();
        var service = new HistoryRetentionService(_db, TimeSpan.FromDays(380));

        var (ok, deleted, error) = service.Cleanup();

        Assert.True(ok);
        Assert.Null(error);
        Assert.Equal(5, deleted);

        // The old rows are gone; the fresh ones survive.
        Assert.Equal(1L, Count("alarms"));
        Assert.Equal(1L, Count("audit_events"));
        Assert.Equal(1L, Count("production_counts"));
        Assert.Equal(1L, Count("comms_records"));
        Assert.Equal(1L, Count("debug_commands"));

        Assert.Equal("fresh", (string)Value("alarms", "message")!);
        Assert.Equal("fresh", (string)Value("audit_events", "target")!);
    }

    [Fact]
    public void Cleanup_BoundaryRowExactlyAtCutoff_IsRetained()
    {
        // A zero-length retention puts the cut-off at "now", so a row at the boundary classifies
        // deterministically under the strict `<` comparison. Wide ±60s margins ensure slow CI clock
        // drift cannot overtake the "future" row while seeding.
        var now = DateTime.UtcNow;
        var production = new ProductionRepository(_db);
        production.AppendProduction(1, now.AddSeconds(-60));
        production.AppendProduction(2, now.AddSeconds(60));

        var service = new HistoryRetentionService(_db, TimeSpan.FromDays(0));
        var (_, _, _) = service.Cleanup();

        // Only the row strictly older than the cut-off is deleted; the boundary row survives.
        Assert.Equal(1L, Count("production_counts"));
    }

    [Fact]
    public void Cleanup_UnavailableDatabase_ReturnsOkFalseWithoutThrowing()
    {
        _db.Dispose();
        DeleteDatabaseFiles();

        var service = new HistoryRetentionService(_db, TimeSpan.FromDays(380));
        (bool ok, int deleted, string? error) = (false, 0, null);

        var ex = Record.Exception(() => { var r = service.Cleanup(); ok = r.ok; deleted = r.deleted; error = r.error; });

        Assert.Null(ex);
        Assert.False(ok);
        Assert.Equal(0, deleted);
        Assert.NotNull(error);
    }

    private long Count(string table) =>
        Convert.ToInt64(_db.Query($"SELECT COUNT(*) AS c FROM {table}")[0]["c"]);

    private object? Value(string table, string column) =>
        _db.Query($"SELECT {column} FROM {table}")[0][column];
}
