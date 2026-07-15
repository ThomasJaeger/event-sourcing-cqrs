using EventSourcingCqrs.Hosts.Workers;
using EventSourcingCqrs.Infrastructure.EventStore.Postgres;
using EventSourcingCqrs.Infrastructure.EventStore.SqlServer;
using EventSourcingCqrs.Infrastructure.Migrations.Postgres;
using Microsoft.Extensions.Hosting;

var eventStoreConnectionString = Environment.GetEnvironmentVariable("EVENT_STORE_CONNECTION_STRING");
if (string.IsNullOrEmpty(eventStoreConnectionString))
{
    Console.Error.WriteLine("EVENT_STORE_CONNECTION_STRING is not set.");
    return 78; // EX_CONFIG
}
var readModelConnectionString = Environment.GetEnvironmentVariable("READ_MODEL_CONNECTION_STRING");
if (string.IsNullOrEmpty(readModelConnectionString))
{
    Console.Error.WriteLine("READ_MODEL_CONNECTION_STRING is not set.");
    return 78;
}

// The provider is read once for the process. This one value governs both the migration branch below
// and the composition branch inside WorkersHostFactory, so the two cannot disagree. A bad value is
// a configuration failure, which this host reports the way it reports a missing one.
EventStoreProvider eventStoreProvider;
try
{
    eventStoreProvider = EventStoreProviderSelection.Read(
        Environment.GetEnvironmentVariable("EVENT_STORE_PROVIDER"));
    EventStoreProviderSelection.ValidateConnectionString(
        eventStoreProvider, eventStoreConnectionString);
}
catch (InvalidOperationException ex)
{
    Console.Error.WriteLine(ex.Message);
    return 78; // EX_CONFIG
}

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
        eventStoreProvider, eventStoreConnectionString, readModelConnectionString);
    await host.RunAsync(cts.Token);
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Host failure: {ex.Message}");
    return 1;
}
