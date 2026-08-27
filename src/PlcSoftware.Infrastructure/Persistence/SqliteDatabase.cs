using Microsoft.Data.Sqlite;

namespace PlcSoftware.Infrastructure.Persistence;

/// <summary>
/// Thin wrapper over a single SQLite database file used for persisting alarms, audit events,
/// production counts, comms records and debug commands (design §4.4 / §6.5).
///
/// The wrapper owns the connection string and the schema; callers use <see cref="InTransaction"/> for
/// write units (commit/rollback) and <see cref="Query"/> for reads. Every value is bound through
/// parameters, never concatenated into SQL, so hostile text cannot break out of a statement.
///
/// The connection string opts into WAL journal mode (committed writes survive process crashes, and
/// readers and a single writer can proceed concurrently) and a single connection string is reused,
/// which makes SQLite honour the single-writer-at-a-time model across the <see cref="InTransaction"/>
/// writes issued by the repositories.
/// </summary>
public sealed class SqliteDatabase : IDisposable
{
    private const string ConnectionStringPrefix = "Data Source=";

    private readonly string _connectionString;
    private bool _disposed;

    public SqliteDatabase(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _connectionString = $"{ConnectionStringPrefix}{path}";

        using var connection = OpenConnection();
        using var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA journal_mode=WAL;";
        pragma.ExecuteNonQuery();
    }

    /// <summary>
    /// Creates the tables and indexes if they do not yet exist. Safe to call more than once on the
    /// same file (every statement is <c>CREATE ... IF NOT EXISTS</c>).
    /// </summary>
    public void EnsureSchema()
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = SchemaSql;
        command.ExecuteNonQuery();
    }

    /// <summary>
    /// Runs <paramref name="action"/> inside a single <see cref="SqliteTransaction"/> with the highest
    /// serializable isolation level. The transaction is committed when the action returns normally and
    /// rolled back when the action throws, so the batch is atomic.
    /// </summary>
    public void InTransaction(Action<SqliteConnection> action)
    {
        ArgumentNullException.ThrowIfNull(action);

        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction(System.Data.IsolationLevel.Serializable);
        try
        {
            action(connection);
            transaction.Commit();
        }
        catch
        {
            transaction.Rollback();
            throw;
        }
    }

    /// <summary>
    /// Executes <paramref name="sql"/> and returns each row as a dictionary of column name to value
    /// (a SQLite <c>NULL</c> column comes back as <c>null</c>). <paramref name="bind"/>, when present,
    /// attaches parameters; values are always bound and never interpolated into <paramref name="sql"/>.
    /// </summary>
    public List<Dictionary<string, object?>> Query(string sql, Action<SqliteCommand>? bind = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sql);

        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        bind?.Invoke(command);

        var results = new List<Dictionary<string, object?>>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var row = new Dictionary<string, object?>(StringComparer.Ordinal);
            for (var i = 0; i < reader.FieldCount; i++)
            {
                row[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i);
            }

            results.Add(row);
        }

        return results;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        SqliteConnection.ClearAllPools();
    }

    private SqliteConnection OpenConnection()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();

        // An explicit busy timeout so concurrent writers wait (up to 5 s) instead of immediately
        // failing with SQLITE_BUSY when the SQLite write lock is held by another connection.
        using var busy = connection.CreateCommand();
        busy.CommandText = "PRAGMA busy_timeout=5000;";
        busy.ExecuteNonQuery();

        return connection;
    }

    private const string SchemaSql = """
        CREATE TABLE IF NOT EXISTS alarms (
            id            INTEGER PRIMARY KEY AUTOINCREMENT,
            code          INTEGER NOT NULL,
            message       TEXT    NOT NULL,
            opened_at     TEXT    NOT NULL,
            closed_at     TEXT
        );

        CREATE INDEX IF NOT EXISTS ix_alarms_code_opened
            ON alarms (code, opened_at);

        CREATE UNIQUE INDEX IF NOT EXISTS ux_alarms_open
            ON alarms (code)
            WHERE closed_at IS NULL;

        CREATE TABLE IF NOT EXISTS audit_events (
            id            INTEGER PRIMARY KEY AUTOINCREMENT,
            category      INTEGER NOT NULL,
            target        TEXT    NOT NULL,
            value_text    TEXT,
            message_text  TEXT,
            recorded_at   TEXT    NOT NULL
        );

        CREATE INDEX IF NOT EXISTS ix_audit_events_recorded_at
            ON audit_events (recorded_at);

        CREATE TABLE IF NOT EXISTS production_counts (
            id          INTEGER PRIMARY KEY AUTOINCREMENT,
            value       INTEGER NOT NULL,
            recorded_at TEXT    NOT NULL
        );

        CREATE INDEX IF NOT EXISTS ix_production_counts_recorded_at
            ON production_counts (recorded_at);

        CREATE TABLE IF NOT EXISTS comms_records (
            id          INTEGER PRIMARY KEY AUTOINCREMENT,
            event       TEXT NOT NULL,
            detail      TEXT,
            recorded_at TEXT NOT NULL
        );

        CREATE INDEX IF NOT EXISTS ix_comms_records_recorded_at
            ON comms_records (recorded_at);

        CREATE TABLE IF NOT EXISTS debug_commands (
            id          INTEGER PRIMARY KEY AUTOINCREMENT,
            target      TEXT NOT NULL,
            address     INTEGER NOT NULL,
            value_text  TEXT,
            recorded_at TEXT NOT NULL
        );

        CREATE INDEX IF NOT EXISTS ix_debug_commands_recorded_at
            ON debug_commands (recorded_at);
        """;
}
