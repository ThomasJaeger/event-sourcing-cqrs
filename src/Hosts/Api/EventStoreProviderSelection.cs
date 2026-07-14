using Microsoft.Data.SqlClient;
using Npgsql;

namespace EventSourcingCqrs.Hosts.Api;

// The event-store engine this host composes (PLAN.md:253). EVENT_STORE_PROVIDER selects it. Absent
// or empty means Postgres, which is what every deployment ran before the key existed, so the default
// path is exactly what it was. An unrecognized value fails the host at startup with the value named:
// there is no fallback, because a typo that silently composes the other engine writes events to the
// wrong database. The in-memory store is a test double, not a provider, and has no value here.
//
// Duplicated per host root rather than shared. The hosts have no common composition project, and
// ADR 0004's posture applies to this too: ten lines in each root beats a project that exists to hold
// them.
internal enum EventStoreProvider
{
    Postgres,
    SqlServer,
}

internal static class EventStoreProviderSelection
{
    public static EventStoreProvider Read(string? configuredValue)
    {
        if (string.IsNullOrWhiteSpace(configuredValue))
        {
            return EventStoreProvider.Postgres;
        }

        if (string.Equals(configuredValue, "Postgres", StringComparison.OrdinalIgnoreCase))
        {
            return EventStoreProvider.Postgres;
        }

        if (string.Equals(configuredValue, "SqlServer", StringComparison.OrdinalIgnoreCase))
        {
            return EventStoreProvider.SqlServer;
        }

        throw new InvalidOperationException(
            $"EVENT_STORE_PROVIDER is '{configuredValue}'. Recognized values are Postgres and SqlServer.");
    }

    // Parses the connection string with the selected engine's builder, so a provider and connection
    // mismatch fails at composition. Both adapters build their data source lazily, so without this
    // the first command is where an operator finds out, and it surfaces as an opaque 500. The guard
    // is loud only where the driver lets it be: SqlClient rejects a PostgreSQL-shaped string at
    // parse, while Npgsql accepts several SQL Server keywords, so a SQL Server string selected as
    // Postgres can still reach the first connect before it fails.
    public static void ValidateConnectionString(EventStoreProvider provider, string connectionString)
    {
        try
        {
            if (provider == EventStoreProvider.SqlServer)
            {
                _ = new SqlConnectionStringBuilder(connectionString);
            }
            else
            {
                _ = new NpgsqlConnectionStringBuilder(connectionString);
            }
        }
        catch (ArgumentException ex)
        {
            // The message names the key, never the value: a connection string carries a password.
            throw new InvalidOperationException(
                $"EVENT_STORE_CONNECTION_STRING is not a valid {provider} connection string.", ex);
        }
    }
}
