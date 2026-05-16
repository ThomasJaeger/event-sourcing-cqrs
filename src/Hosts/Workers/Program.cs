using EventSourcingCqrs.Hosts.Workers;
using EventSourcingCqrs.Infrastructure.EventStore.Postgres;
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

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

try
{
    // Event-store migrations first: migration 0005's pg_notify trigger must be
    // in place before the host's listener starts. If the read-model connection
    // string differs (split-database deployment), run again against it; only
    // the read_models schema migrations land there. Ordinal compare so a
    // trailing-semicolon or whitespace difference does not skip the second
    // run when the operator intends a separate database.
    var runner = new MigrationRunner(
        EventStorePostgresMigrations.Assembly,
        EventStorePostgresMigrations.ResourcePrefix);
    await runner.RunPendingAsync(
        new MigrationRunnerOptions
        {
            ConnectionString = eventStoreConnectionString,
            Log = Console.WriteLine,
        },
        cts.Token);
    if (!string.Equals(eventStoreConnectionString, readModelConnectionString, StringComparison.Ordinal))
    {
        await runner.RunPendingAsync(
            new MigrationRunnerOptions
            {
                ConnectionString = readModelConnectionString,
                Log = Console.WriteLine,
            },
            cts.Token);
    }
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
    using var host = WorkersHostFactory.Build(eventStoreConnectionString, readModelConnectionString);
    await host.RunAsync(cts.Token);
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Host failure: {ex.Message}");
    return 1;
}
