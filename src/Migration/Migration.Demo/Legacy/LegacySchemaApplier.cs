using System.Text;
using Npgsql;

namespace EventSourcingCqrs.Migration.Demo.Legacy;

// Chapter 18: applies the CRUD-shaped legacy schema from an embedded SQL resource. It creates the
// orders and order_lines tables and the change-tracking trigger that appends every write to
// legacy.legacy_changes, the table the demo's CDC pattern reads to emit domain events. The schema
// is idempotent, so applying it against an already-provisioned database changes nothing.
public sealed class LegacySchemaApplier
{
    private const string SchemaResourceName = "Migration.Demo.Legacy.legacy_schema.sql";

    public async Task ApplyAsync(string connectionString, CancellationToken cancellationToken)
    {
        var sql = ReadSchema();
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static string ReadSchema()
    {
        var assembly = typeof(LegacySchemaApplier).Assembly;
        using var stream = assembly.GetManifestResourceStream(SchemaResourceName)
            ?? throw new InvalidOperationException(
                $"Embedded legacy schema '{SchemaResourceName}' could not be opened.");
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return Encoding.UTF8.GetString(memory.ToArray());
    }
}
