using EventSourcingCqrs.Domain.Abstractions;

namespace EventSourcingCqrs.Infrastructure.EventStore.Postgres;

// Reads the head of the global event stream for operational lag reporting.
// COALESCE(MAX(global_position), 0) returns 0 for an empty store in SQL, so there
// is one code path and no C# null-map. Opens a connection through the same
// INpgsqlConnectionFactory the event store uses, so it reads the same database;
// the read is read-only and opens no transaction.
public sealed class PostgresEventStoreHeadReader : IEventStoreHeadPosition
{
    private readonly INpgsqlConnectionFactory _factory;

    public PostgresEventStoreHeadReader(INpgsqlConnectionFactory factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        _factory = factory;
    }

    public async Task<long> GetHeadPositionAsync(CancellationToken ct)
    {
        await using var connection = await _factory.OpenConnectionAsync(ct);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT COALESCE(MAX(global_position), 0) FROM event_store.events";
        return (long)(await cmd.ExecuteScalarAsync(ct))!;
    }
}
