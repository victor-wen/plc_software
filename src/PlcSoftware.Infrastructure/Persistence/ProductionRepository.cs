namespace PlcSoftware.Infrastructure.Persistence;

/// <summary>
/// Persists production, comms and debug-command records (design §4.4 / §6.5). All are append-only
/// historical rows; values are bound as parameters.
/// </summary>
public sealed class ProductionRepository
{
    private readonly SqliteDatabase _db;

    public ProductionRepository(SqliteDatabase database)
    {
        _db = database ?? throw new ArgumentNullException(nameof(database));
    }

    /// <summary>Appends one production-count sample.</summary>
    public void AppendProduction(long countValue, DateTime at)
    {
        _db.InTransaction(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO production_counts (value, recorded_at)
                VALUES (@value, @recordedAt)
                """;
            command.Parameters.AddWithValue("@value", countValue);
            command.Parameters.AddWithValue("@recordedAt", at.ToString("o"));
            command.ExecuteNonQuery();
        });
    }

    /// <summary>Appends one comms/log record with a free-form detail.</summary>
    public void AppendComms(string @event, string? detail, DateTime at)
    {
        _db.InTransaction(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO comms_records (event, detail, recorded_at)
                VALUES (@event, @detail, @recordedAt)
                """;
            command.Parameters.AddWithValue("@event", @event);
            command.Parameters.AddWithValue("@detail", detail ?? (object)DBNull.Value);
            command.Parameters.AddWithValue("@recordedAt", at.ToString("o"));
            command.ExecuteNonQuery();
        });
    }

    /// <summary>Appends one debug-command record.</summary>
    public void AppendDebugCommand(string target, int address, string? valueText, DateTime at)
    {
        _db.InTransaction(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO debug_commands (target, address, value_text, recorded_at)
                VALUES (@target, @address, @valueText, @recordedAt)
                """;
            command.Parameters.AddWithValue("@target", target);
            command.Parameters.AddWithValue("@address", address);
            command.Parameters.AddWithValue("@valueText", valueText ?? (object)DBNull.Value);
            command.Parameters.AddWithValue("@recordedAt", at.ToString("o"));
            command.ExecuteNonQuery();
        });
    }
}
