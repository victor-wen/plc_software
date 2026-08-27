using PlcSoftware.Core.Abstractions;

namespace PlcSoftware.Infrastructure.Persistence;

/// <summary>
/// Persists host-write audit events (design §6.5 / audit surface): category, target, value and message,
/// timestamped by the caller. Every value is bound as a parameter, never concatenated into SQL.
/// </summary>
public sealed class AuditRepository
{
    private readonly SqliteDatabase _db;

    public AuditRepository(SqliteDatabase database)
    {
        _db = database ?? throw new ArgumentNullException(nameof(database));
    }

    /// <summary>Persists one audit event at <paramref name="timestamp"/>.</summary>
    public void Record(AuditEvent auditEvent, DateTime timestamp)
    {
        ArgumentNullException.ThrowIfNull(auditEvent);

        _db.InTransaction(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO audit_events (category, target, value_text, message_text, recorded_at)
                VALUES (@category, @target, @value, @message, @recordedAt)
                """;
            command.Parameters.AddWithValue("@category", (int)auditEvent.Category);
            command.Parameters.AddWithValue("@target", auditEvent.Target);
            command.Parameters.AddWithValue("@value", auditEvent.Value?.ToString() ?? (object)DBNull.Value);
            command.Parameters.AddWithValue("@message", auditEvent.Message ?? (object)DBNull.Value);
            command.Parameters.AddWithValue("@recordedAt", timestamp.ToString("o"));
            command.ExecuteNonQuery();
        });
    }

    /// <summary>Returns the most recent <paramref name="count"/> events, newest first.</summary>
    public List<Dictionary<string, object?>> GetRecent(int count)
        => _db.Query(
            "SELECT category, target, value_text, message_text, recorded_at FROM audit_events ORDER BY id DESC LIMIT @limit",
            command => command.Parameters.AddWithValue("@limit", count));
}
