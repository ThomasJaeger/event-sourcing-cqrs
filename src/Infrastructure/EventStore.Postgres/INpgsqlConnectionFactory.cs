using Npgsql;

namespace EventSourcingCqrs.Infrastructure.EventStore.Postgres;

// PostgreSQL-specific by design. Returning the typed NpgsqlConnection
// keeps the adapter on the typed Npgsql API; lifting this interface to
// Domain.Abstractions would force a DbConnection return and lose that.
// Per ADR 0004 the SQL Server adapter declares its own factory interface.
public interface INpgsqlConnectionFactory
{
    Task<NpgsqlConnection> OpenConnectionAsync(CancellationToken ct);
}
