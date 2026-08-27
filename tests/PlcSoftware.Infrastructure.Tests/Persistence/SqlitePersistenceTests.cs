using PlcSoftware.Core.Abstractions;
using PlcSoftware.Core.Models;
using PlcSoftware.Infrastructure.Persistence;

namespace PlcSoftware.Infrastructure.Tests.Persistence;

/// <summary>
/// Behavioural tests for the SQLite persistence layer: the database wrapper and the alarm, audit and
/// production repositories (design §4.4 / §6.5 persistence).
///
/// Verified rules:
///   - creating the database and running <see cref="SqliteDatabase.EnsureSchema"/> twice on the same
///     file is idempotent (no exception);
///   - <see cref="SqliteDatabase.InTransaction"/> really commits and rolls back: a row written inside a
///     transaction is visible after commit, and is not persisted when the action throws;
///   - every value is bound via parameters, so hostile text ("'; DROP TABLE ...") and Chinese text are
///     stored verbatim and cannot break out of the SQL;
///   - the database accepts a burst of concurrent single-writer inserts without corruption;
///   - a persistently-active alarm is not duplicated (InsertStarted twice yields a single open row);
///   - 50 concurrent same-code inserts yield exactly one open row (the rest are rejected atomically);
///   - recovery closes the most recent open alarm row (CloseMostRecentOpen sets closed_at and clears
///     open rows).
/// </summary>
public class SqlitePersistenceTests : IDisposable
{
    private readonly string _dbPath;
    private readonly SqliteDatabase _db;

    public SqlitePersistenceTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"plc-test-{Guid.NewGuid():N}.db");
        _db = new SqliteDatabase(_dbPath);
        _db.EnsureSchema();
    }

    public void Dispose()
    {
        _db.Dispose();
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

    [Fact]
    public void BuildingDatabaseTwice_OnSameFile_IsIdempotent()
    {
        // Re-open the same file and run EnsureSchema a second time: it must not throw.
        using var second = new SqliteDatabase(_dbPath);

        var ex = Record.Exception(second.EnsureSchema);

        Assert.Null(ex);
    }

    [Fact]
    public void InTransaction_Commit_MakesInsertVisible()
    {
        _db.InTransaction(conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "INSERT INTO production_counts (value, recorded_at) VALUES (@v, @t)";
            cmd.Parameters.AddWithValue("@v", 100);
            cmd.Parameters.AddWithValue("@t", "2026-08-27T10:00:00");
            cmd.ExecuteNonQuery();
        });

        var rows = _db.Query("SELECT value FROM production_counts");
        Assert.Single(rows);
        Assert.Equal(100L, Convert.ToInt64(rows[0]["value"]));
    }

    [Fact]
    public void InTransaction_RollbackOnException_DoesNotPersist()
    {
        Assert.Throws<InvalidOperationException>(() =>
            _db.InTransaction(conn =>
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "INSERT INTO production_counts (value, recorded_at) VALUES (@v, @t)";
                cmd.Parameters.AddWithValue("@v", 42);
                cmd.Parameters.AddWithValue("@t", "2026-08-27T10:00:00");
                cmd.ExecuteNonQuery();

                throw new InvalidOperationException("boom");
            }));

        var rows = _db.Query("SELECT value FROM production_counts");
        Assert.Empty(rows);
    }

    [Fact]
    public void AuditRepository_ParameterizedTarget_StoresHostileAndChineseTextVerbatim()
    {
        const string hostile = "'; DROP TABLE audit_events; --";
        const string chinese = "屏蔽 光栅 参数 调试";
        var repo = new AuditRepository(_db);

        repo.Record(new AuditEvent(AuditCategory.Parameter, hostile, 123, chinese), new DateTime(2026, 8, 27, 10, 30, 0));

        // The hostile target and Chinese message must be stored intact...
        var rows = _db.Query("SELECT target, message_text FROM audit_events");
        Assert.Single(rows);
        Assert.Equal(hostile, (string)rows[0]["target"]!);
        Assert.Equal(chinese, (string)rows[0]["message_text"]!);

        // ...and the table must still exist (the injection must not have executed).
        var hasTable = _db.Query(
            "SELECT name FROM sqlite_master WHERE type='table' AND name='audit_events'");
        Assert.Single(hasTable);
    }

    [Fact]
    public void AuditRepository_FiftyConcurrentRecords_AllPersistWithoutCorruption()
    {
        var repo = new AuditRepository(_db);

        var options = new ParallelOptions { MaxDegreeOfParallelism = 50 };
        Parallel.For(0, 50, options, i =>
            repo.Record(
                new AuditEvent(AuditCategory.Mask, $"M110-{i}", i, $"msg {i}"),
                new DateTime(2026, 8, 27, 10, 30, 0)));

        var count = _db.Query("SELECT COUNT(*) AS c FROM audit_events");
        Assert.Equal(50L, Convert.ToInt64(count[0]["c"]));
    }

    [Fact]
    public void AlarmRepository_InsertStartedTwiceWithoutClose_KeepsSingleOpenRow()
    {
        var repo = new AlarmRepository(_db);
        var definition = new FaultDefinition { Code = 1, Message = "K1 急停" };

        repo.InsertStarted(definition, new DateTime(2026, 8, 27, 9, 0, 0));
        repo.InsertStarted(definition, new DateTime(2026, 8, 27, 9, 5, 0));

        var open = repo.QueryOpen();
        Assert.Single(open);
        Assert.Equal(1, Convert.ToInt32(open[0]["code"]!));
        Assert.Null(open[0]["closed_at"]);
    }

    [Fact]
    public void AlarmRepository_CloseMostRecentOpen_SetsClosedAtAndClearsOpenRows()
    {
        var repo = new AlarmRepository(_db);
        var definition = new FaultDefinition { Code = 1, Message = "K1 急停" };

        repo.InsertStarted(definition, new DateTime(2026, 8, 27, 9, 0, 0));
        repo.CloseMostRecentOpen(new DateTime(2026, 8, 27, 9, 6, 30));

        Assert.Empty(repo.QueryOpen());

        // The closed row must carry the recovery timestamp.
        var closed = _db.Query("SELECT code, closed_at FROM alarms");
        Assert.Single(closed);
        Assert.Equal(1, Convert.ToInt32(closed[0]["code"]!));
        Assert.NotNull(closed[0]["closed_at"]);
    }

    [Fact]
    public void AlarmRepository_FiftyConcurrentSameCodeInserts_ExactlyOneOpenRow()
    {
        var repo = new AlarmRepository(_db);
        var definition = new FaultDefinition { Code = 1, Message = "K1 急停" };

        var options = new ParallelOptions { MaxDegreeOfParallelism = 50 };
        Parallel.For(0, 50, options, i =>
            repo.InsertStarted(definition, new DateTime(2026, 8, 27, 9, 0, 0).AddSeconds(i)));

        // Exactly one open row survives: every concurrent duplicate is rejected atomically (either
        // silently — the INSERT ... WHERE NOT EXISTS guard sees the committed row — or, in a genuine
        // race, via the unique-index conflict surfaced as InvalidOperationException).
        var open = repo.QueryOpen();
        Assert.Single(open);
        Assert.Equal(1, Convert.ToInt32(open[0]["code"]!));
        Assert.Null(open[0]["closed_at"]);

        // No partial duplicates: the table holds exactly the one open row and nothing else.
        var all = _db.Query("SELECT COUNT(*) AS c FROM alarms");
        Assert.Equal(1L, Convert.ToInt64(all[0]["c"]));
    }
}
