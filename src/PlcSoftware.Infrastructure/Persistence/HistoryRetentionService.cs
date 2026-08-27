using Microsoft.Data.Sqlite;

namespace PlcSoftware.Infrastructure.Persistence;

/// <summary>
/// Deletes historical rows older than a retention period (design §6.5 data retention).
///
/// The cut-off is <c>UtcNow - retention</c>, serialised as an ISO-8601 string and bound as a parameter, so
/// the comparison is lexicographic over ISO timestamps — exactly the format the repositories persist via
/// <c>DateTime.ToString("o")</c>. All five history tables are purged in a single transaction so the
/// deletions either all happen or none do. Any <see cref="SqliteException"/> (e.g. a closed database) is
/// caught and surfaced as a tuple rather than thrown, so callers are never crashed by a cleanup sweep.
/// </summary>
public sealed class HistoryRetentionService
{
    private readonly SqliteDatabase _db;
    private readonly TimeSpan _retention;

    public HistoryRetentionService(SqliteDatabase db, TimeSpan? retention = null)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _retention = retention ?? TimeSpan.FromDays(365);
    }

    /// <summary>
    /// Deletes rows strictly older than the retention cut-off from every history table. Returns whether the
    /// sweep succeeded, how many rows were deleted, and (on failure) a non-null error message. Never throws.
    /// </summary>
    public (bool ok, int deleted, string? error) Cleanup()
    {
        var cutOff = DateTime.UtcNow.Subtract(_retention).ToString("o");

        var tables = new[]
        {
            ("alarms", "opened_at"),
            ("audit_events", "recorded_at"),
            ("production_counts", "recorded_at"),
            ("comms_records", "recorded_at"),
            ("debug_commands", "recorded_at"),
        };

        try
        {
            var deleted = 0;
            _db.InTransaction(connection =>
            {
                foreach (var (table, column) in tables)
                {
                    using var command = connection.CreateCommand();
                    command.CommandText =
                        $"DELETE FROM {table} WHERE {column} < @cutOff";
                    command.Parameters.AddWithValue("@cutOff", cutOff);
                    deleted += command.ExecuteNonQuery();
                }
            });

            return (true, deleted, null);
        }
        catch (SqliteException ex)
        {
            return (false, 0, ex.Message);
        }
    }
}
