using Microsoft.Data.SqlClient;

namespace EventSourcingCqrs.Infrastructure.EventStore.SqlServer;

// SQL Server specific by design, and the twin the PostgreSQL adapter's INpgsqlConnectionFactory
// comment already anticipates. Returning the typed SqlConnection keeps the adapter on the typed
// Microsoft.Data.SqlClient API; lifting this to Domain.Abstractions would force a DbConnection
// return and lose it. Per ADR 0004, each adapter declares its own factory.
public interface ISqlServerConnectionFactory
{
    Task<SqlConnection> OpenConnectionAsync(CancellationToken ct);
}
