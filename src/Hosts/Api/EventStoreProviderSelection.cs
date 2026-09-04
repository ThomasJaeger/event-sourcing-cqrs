using KurrentDB.Client;
using Microsoft.Data.SqlClient;
using Npgsql;

namespace EventSourcingCqrs.Hosts.Api;

// The event-store engine this host composes (the provider-switch commitment in docs/PLAN.md's scope). EVENT_STORE_PROVIDER selects it. Absent
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
    Kurrent,
    DynamoDb,
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

        if (string.Equals(configuredValue, "Kurrent", StringComparison.OrdinalIgnoreCase))
        {
            return EventStoreProvider.Kurrent;
        }

        if (string.Equals(configuredValue, "DynamoDb", StringComparison.OrdinalIgnoreCase))
        {
            return EventStoreProvider.DynamoDb;
        }

        throw new InvalidOperationException(
            $"EVENT_STORE_PROVIDER is '{configuredValue}'. Recognized values are Postgres, SqlServer, " +
            $"Kurrent, and DynamoDb.");
    }

    // Parses the connection string with the selected engine's builder, so a provider and connection
    // mismatch fails at composition. The relational adapters build their data source lazily, so
    // without this the first command is where an operator finds out, and it surfaces as an opaque
    // 500. The guard is loud only where the driver lets it be: SqlClient rejects a PostgreSQL-shaped
    // string at parse, while Npgsql accepts several SQL Server keywords, so a SQL Server string
    // selected as Postgres can still reach the first connect before it fails.
    //
    // The seam validates provider configuration, DSN-shaped or not. DynamoDB is addressed by its own
    // two keys and has no connection string at all, so its arm checks those keys and never reads the
    // one above. The name is the DSN-era one every call site spells; the job outgrew it.
    public static void ValidateConnectionString(
        EventStoreProvider provider,
        string connectionString,
        string? dynamoDbServiceUrl = null,
        string? dynamoDbTableName = null)
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
                case EventStoreProvider.DynamoDb:
                    ValidateDynamoDbConfiguration(dynamoDbServiceUrl, dynamoDbTableName);
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

    // There is no builder to parse here, so the check is on the shape of the two keys that address
    // the engine. Both messages name the value, unlike the wrap above: that rule is about secrets,
    // and an endpoint and a table name are neither. An operator who mistyped one needs to see what
    // the host read, which is how Read reports a bad provider value too.
    //
    // These throw InvalidOperationException rather than ArgumentException on purpose. The filter
    // above would otherwise relabel them as an invalid EVENT_STORE_CONNECTION_STRING, naming a key
    // that has nothing to do with the fault.
    private static void ValidateDynamoDbConfiguration(string? serviceUrl, string? tableName)
    {
        if (string.IsNullOrWhiteSpace(serviceUrl))
        {
            throw new InvalidOperationException(
                "EVENT_STORE_DYNAMODB_SERVICE_URL is not set. The DynamoDb provider is addressed by "
                + "a service URL, not a connection string.");
        }

        // Uri.TryCreate rather than new Uri: UriFormatException is a FormatException, which neither
        // the filter above nor the Workers host's configuration handler catches, so a throwing parse
        // would crash a host that owes its operator an exit code. The scheme check is the substance:
        // "localhost:4566" parses absolute with scheme "localhost" and an esdb:// string parses too,
        // so absoluteness alone waves the dropped scheme and the pasted Kurrent DSN through to fail
        // at the first append, which is the failure this seam exists to move forward.
        if (!Uri.TryCreate(serviceUrl, UriKind.Absolute, out var serviceUri)
            || (serviceUri.Scheme != Uri.UriSchemeHttp && serviceUri.Scheme != Uri.UriSchemeHttps))
        {
            throw new InvalidOperationException(
                $"EVENT_STORE_DYNAMODB_SERVICE_URL is '{serviceUrl}'. It must be an absolute http or "
                + "https URL, such as http://localhost:4566.");
        }

        // Unset means the adapter's default table, which every host computes identically and is the
        // only state where two hosts agree without an operator saying so twice. Set but blank is a
        // deploy bug the default would absorb in silence.
        if (tableName is not null && string.IsNullOrWhiteSpace(tableName))
        {
            throw new InvalidOperationException(
                "EVENT_STORE_DYNAMODB_TABLE_NAME is set but blank. Unset it to take the adapter's "
                + "default table, or name the table that holds the events.");
        }
    }
}
