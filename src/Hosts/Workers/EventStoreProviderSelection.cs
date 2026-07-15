using KurrentDB.Client;
using Microsoft.Data.SqlClient;
using Npgsql;

namespace EventSourcingCqrs.Hosts.Workers;

// The event-store engine this host composes (PLAN.md:253). EVENT_STORE_PROVIDER selects it. Absent
// or empty means Postgres, which is what every deployment ran before the key existed, so the default
// path is exactly what it was. An unrecognized value fails the host at startup with the value named:
// there is no fallback, because a typo that silently composes the other engine writes events to the
// wrong database. The in-memory store is a test double, not a provider, and has no value here.
//
// Public because Program.cs reads the key once and hands the result to WorkersHostFactory.Build,
// which is also how the Workers tests state the engine they compose. Duplicated per host root rather
// than shared: the hosts have no common composition project, and ADR 0004's posture applies to this
// too.
public enum EventStoreProvider
{
    Postgres,
    SqlServer,
    Kurrent,
}

public static class EventStoreProviderSelection
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

        if (string.Equals(configuredValue, "Kurrent", StringComparison.OrdinalIgnoreCase))
        {
            return EventStoreProvider.Kurrent;
        }

        throw new InvalidOperationException(
            $"EVENT_STORE_PROVIDER is '{configuredValue}'. Recognized values are Postgres, SqlServer, and Kurrent.");
    }

    // Parses the connection string with the selected engine's builder, so a provider and connection
    // mismatch fails before the migration runner opens anything. The guard is loud only where the
    // driver lets it be: SqlClient rejects a PostgreSQL-shaped string at parse, while Npgsql accepts
    // several SQL Server keywords, so a SQL Server string selected as Postgres can still reach the
    // first connect before it fails.
    public static void ValidateConnectionString(EventStoreProvider provider, string connectionString)
    {
        try
        {
            switch (provider)
            {
                case EventStoreProvider.SqlServer:
                    _ = new SqlConnectionStringBuilder(connectionString);
                    break;
                case EventStoreProvider.Postgres:
                    _ = new NpgsqlConnectionStringBuilder(connectionString);
                    break;
                case EventStoreProvider.Kurrent:
                    // KurrentDB has no ADO.NET builder; its client parses the esdb:// string, and a
                    // malformed one raises ConnectionStringParseException.
                    _ = KurrentDBClientSettings.Create(connectionString);
                    break;
                default:
                    throw new InvalidOperationException(
                        $"Unhandled event store provider: {provider}.");
            }
        }
        catch (Exception ex) when (ex is ArgumentException or ConnectionStringParseException)
        {
            // The message names the key, never the value: a connection string carries a password.
            throw new InvalidOperationException(
                $"EVENT_STORE_CONNECTION_STRING is not a valid {provider} connection string.", ex);
        }
    }
}
