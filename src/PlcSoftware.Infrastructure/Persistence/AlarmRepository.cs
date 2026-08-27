using Microsoft.Data.Sqlite;
using PlcSoftware.Core.Models;

namespace PlcSoftware.Infrastructure.Persistence;

/// <summary>
/// Persists the K1-K7 alarm lifecycle (design §4.4). One <see cref="FaultDefinition"/> that stays
/// active is recorded once: inserting an alarm whose code already has an open (not yet closed) row is a
/// no-op, so a persistent alarm is never duplicated. Recovery closes the most recent open row (one
/// active alarm at a time, matching <see cref="PlcSoftware.Core.Services.AlarmService"/>).
/// </summary>
public sealed class AlarmRepository
{
    private readonly SqliteDatabase _db;

    public AlarmRepository(SqliteDatabase database)
    {
        _db = database ?? throw new ArgumentNullException(nameof(database));
    }

    /// <summary>
    /// Records a newly-started alarm. If the same <paramref name="definition"/> code already has an open
    /// (closed_at IS NULL) row, nothing is inserted — a still-active alarm must not be duplicated. The
    /// dedup is atomic: a partial unique index (<c>ux_alarms_open</c>) enforces at most one open row per
    /// code, and the insert uses an <c>INSERT ... WHERE NOT EXISTS</c> guard. A duplicate raises
    /// <see cref="InvalidOperationException"/> so concurrent same-code inserts yield exactly one row.
    /// </summary>
    public void InsertStarted(FaultDefinition definition, DateTime openedAt)
    {
        ArgumentNullException.ThrowIfNull(definition);

        _db.InTransaction(connection =>
        {
            try
            {
                using var command = connection.CreateCommand();
                command.CommandText = """
                    INSERT INTO alarms (code, message, opened_at, closed_at)
                    SELECT @code, @message, @openedAt, NULL
                    WHERE NOT EXISTS (
                        SELECT 1 FROM alarms
                        WHERE code = @code AND closed_at IS NULL
                    )
                    """;
                command.Parameters.AddWithValue("@code", definition.Code);
                command.Parameters.AddWithValue("@message", definition.Message);
                command.Parameters.AddWithValue("@openedAt", openedAt.ToString("o"));
                command.ExecuteNonQuery();
            }
            catch (SqliteException ex) when (ex.SqliteErrorCode == 19)
            {
                throw new InvalidOperationException(
                    $"duplicate open alarm for code {definition.Code}", ex);
            }
        });
    }

    /// <summary>
    /// Closes the most recently opened alarm row (sets <c>closed_at</c>), marking recovery of the single
    /// active alarm. Rows with no open state are left untouched.
    /// </summary>
    public void CloseMostRecentOpen(DateTime closedAt)
    {
        _db.InTransaction(connection =>
        {
            using var find = connection.CreateCommand();
            find.CommandText = """
                SELECT id FROM alarms
                WHERE closed_at IS NULL
                ORDER BY opened_at DESC, id DESC
                LIMIT 1
                """;
            var id = find.ExecuteScalar();
            if (id is null)
            {
                return;
            }

            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE alarms SET closed_at = @closedAt WHERE id = @id";
            command.Parameters.AddWithValue("@closedAt", closedAt.ToString("o"));
            command.Parameters.AddWithValue("@id", id);
            command.ExecuteNonQuery();
        });
    }

    /// <summary>Returns all open (not yet recovered) alarm rows for inspection.</summary>
    public List<Dictionary<string, object?>> QueryOpen()
        => _db.Query("SELECT id, code, message, opened_at, closed_at FROM alarms WHERE closed_at IS NULL");

    /// <summary>
    /// Returns opened_at rows within [from, to] (either bound may be null = unbounded), newest first.
    /// </summary>
    public List<Dictionary<string, object?>> QueryOpened(DateTime? from, DateTime? to)
        => _db.Query(
            """
            SELECT id, code, message, opened_at, closed_at FROM alarms
            WHERE (@from IS NULL OR opened_at >= @from)
              AND (@to IS NULL OR opened_at <= @to)
            ORDER BY opened_at DESC, id DESC
            """,
            command =>
            {
                command.Parameters.AddWithValue("@from", from?.ToString("o") ?? (object)DBNull.Value);
                command.Parameters.AddWithValue("@to", to?.ToString("o") ?? (object)DBNull.Value);
            });
}
