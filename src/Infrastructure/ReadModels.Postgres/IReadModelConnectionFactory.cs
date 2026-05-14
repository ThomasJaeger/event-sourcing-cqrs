using Npgsql;

namespace EventSourcingCqrs.Infrastructure.ReadModels.Postgres;

// Mirrors INpgsqlConnectionFactory from the event-store adapter. Separate
// factory, separate connection string: read models and the event store share
// one PostgreSQL database in v1, but keeping the factories apart makes the
// eventual split-database move a configuration change. PostgreSQL-specific by
// design; the typed NpgsqlConnection return keeps callers on the typed
// Npgsql API.
public interface IReadModelConnectionFactory
{
    Task<NpgsqlConnection> OpenConnectionAsync(CancellationToken ct);
}
