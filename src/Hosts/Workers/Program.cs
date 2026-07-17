using EventSourcingCqrs.Hosts.Workers;
using EventSourcingCqrs.Infrastructure.EventStore.DynamoDb;
using EventSourcingCqrs.Infrastructure.EventStore.Postgres;
using EventSourcingCqrs.Infrastructure.EventStore.SqlServer;
using EventSourcingCqrs.Infrastructure.Migrations.Postgres;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

// The provider is read once for the process. This one value governs which key addresses the engine,
// the migration branch below, and the composition branch inside WorkersHostFactory, so none of the
// three can disagree. A bad value is a configuration failure, which this host reports the way it
// reports a missing one.
//
// DynamoDB is addressed by a service URL and a table, not a DSN, so this host does not demand a
// connection string on that provider. See the Api host for why the absence is the guard: a required
// key the engine never reads gets filled with the DSN an operator is migrating off, and a dropped
// provider key then composes Postgres against it in silence.
EventStoreProvider eventStoreProvider;
string eventStoreConnectionString;
string? dynamoDbServiceUrl;
string? dynamoDbTableName;
try
{
    eventStoreProvider = EventStoreProviderSelection.Read(
        Environment.GetEnvironmentVariable("EVENT_STORE_PROVIDER"));
    dynamoDbServiceUrl = Environment.GetEnvironmentVariable("EVENT_STORE_DYNAMODB_SERVICE_URL");
    dynamoDbTableName = Environment.GetEnvironmentVariable("EVENT_STORE_DYNAMODB_TABLE_NAME");
    if (eventStoreProvider is EventStoreProvider.DynamoDb)
    {
        eventStoreConnectionString = string.Empty;
    }
    else
    {
        eventStoreConnectionString =
            Environment.GetEnvironmentVariable("EVENT_STORE_CONNECTION_STRING") ?? string.Empty;
        if (string.IsNullOrEmpty(eventStoreConnectionString))
        {
            throw new InvalidOperationException("EVENT_STORE_CONNECTION_STRING is not set.");
        }
    }
    EventStoreProviderSelection.ValidateConnectionString(
        eventStoreProvider, eventStoreConnectionString, dynamoDbServiceUrl, dynamoDbTableName);
}
catch (InvalidOperationException ex)
{
    Console.Error.WriteLine(ex.Message);
    return 78; // EX_CONFIG
}

var readModelConnectionString = Environment.GetEnvironmentVariable("READ_MODEL_CONNECTION_STRING");
if (string.IsNullOrEmpty(readModelConnectionString))
{
    Console.Error.WriteLine("READ_MODEL_CONNECTION_STRING is not set.");
    return 78;
}

// The address the composition seam takes: the DSN on the engines that have one, the service URL on
// the one that does not. Both were validated above, so both are set here.
var eventStoreAddress = eventStoreProvider is EventStoreProvider.DynamoDb
    ? dynamoDbServiceUrl!
    : eventStoreConnectionString;

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

try
{
    // Migration is per database, not per host. The event-store database gets the selected provider's
    // runner and never the other engine's. It runs first because migration 0005's pg_notify trigger
    // must be in place before the PostgreSQL host's listener starts.
    await (eventStoreProvider switch
    {
        EventStoreProvider.SqlServer => new SqlServerMigrationRunner(
                EventStoreSqlServerMigrations.Assembly,
                EventStoreSqlServerMigrations.ResourcePrefix)
            .RunPendingAsync(
                new SqlServerMigrationRunnerOptions
                {
                    ConnectionString = eventStoreConnectionString,
                    Log = Console.WriteLine,
                },
                cts.Token),
        EventStoreProvider.Postgres => new MigrationRunner(
                EventStorePostgresMigrations.Assembly,
                EventStorePostgresMigrations.ResourcePrefix)
            .RunPendingAsync(
                new MigrationRunnerOptions
                {
                    ConnectionString = eventStoreConnectionString,
                    Log = Console.WriteLine,
                },
                cts.Token),
        // KurrentDB manages its own storage; there is no event-store schema to migrate. The
        // read-model PostgreSQL run below stays untouched and unconditional.
        EventStoreProvider.Kurrent => Task.CompletedTask,
        // DynamoDB has no migration set either, but it does have a schema: the table is the schema.
        // The provisioner is the relational runners' analog and stands where they stand, for the
        // reason they do. The table has to exist before anything opens it, the schema is a
        // deployment concern, and this host owns deployment so the Api never races it.
        EventStoreProvider.DynamoDb => EnsureDynamoDbTableAsync(
            dynamoDbServiceUrl!, dynamoDbTableName, cts.Token),
        _ => throw new InvalidOperationException(
            $"Unhandled event store provider: {eventStoreProvider}."),
    });

    // The read-model database is PostgreSQL regardless of the provider, and the read_models schema
    // exists only in the PostgreSQL migration set, so this run is unconditional: on the SqlServer or
    // Kurrent provider nothing else would create it. The set is not split by schema, so the run also creates an
    // event_store schema in the read-model database. Those tables stay empty, because the write side
    // lives in the event-store database on whichever engine, and when both keys name one database
    // the second pass applies nothing at all: the runner tracks what it has applied. The inert
    // schema is the accepted cost of one undivided migration set.
    await new MigrationRunner(
            EventStorePostgresMigrations.Assembly,
            EventStorePostgresMigrations.ResourcePrefix)
        .RunPendingAsync(
            new MigrationRunnerOptions
            {
                ConnectionString = readModelConnectionString,
                Log = Console.WriteLine,
            },
            cts.Token);
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Migration failure: {ex.Message}");
    return 1;
}

try
{
    // using var: Host.Dispose is implemented as sync-over-async to
    // DisposeAsync, which routes through ServiceProvider.DisposeAsync and
    // disposes IAsyncDisposable-only singletons (NpgsqlReadModelConnectionFactory)
    // correctly. The bare ServiceProvider.Dispose path would throw on that
    // singleton; the Host wrapper closes the gap.
    using var host = WorkersHostFactory.Build(
        eventStoreProvider, eventStoreAddress, readModelConnectionString, dynamoDbTableName);
    await host.RunAsync(cts.Token);
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Host failure: {ex.Message}");
    return 1;
}

// The provisioner and its client come from the adapter's own registration rather than a hand-built
// client, so the migration reaches the table through the endpoint and credential resolution the host
// below will read and write it with. A second client here would be a second copy of that policy, and
// a table provisioned against one endpoint or identity and appended to from another fails in ways
// nothing here would catch. This branch runs before any host container exists, which is why it
// composes its own; the container owns the client it built, so disposing the provider closes it and
// the arm above stays an expression. EnsureTableAsync tolerates losing the race and an already
// seeded counter, so every boot and every scale-out lands on one table.
static async Task EnsureDynamoDbTableAsync(
    string serviceUrl, string? tableName, CancellationToken cancellationToken)
{
    var services = new ServiceCollection();
    services.AddDynamoDbEventStore(opts =>
    {
        opts.ServiceUrl = serviceUrl;
        if (!string.IsNullOrEmpty(tableName))
        {
            opts.TableName = tableName;
        }
    });
    await using var provider = services.BuildServiceProvider();
    await provider.GetRequiredService<DynamoDbTableProvisioner>()
        .EnsureTableAsync(cancellationToken);
}
