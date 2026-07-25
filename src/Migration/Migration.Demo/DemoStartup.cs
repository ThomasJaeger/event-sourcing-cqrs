using System.Text.Json;
using EventSourcingCqrs.Application;
using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.Domain.Sales;
using EventSourcingCqrs.Infrastructure.EventStore.Postgres;
using EventSourcingCqrs.Infrastructure.Migrations.Postgres;
using EventSourcingCqrs.Infrastructure.Versioning;
using EventSourcingCqrs.Migration.Demo.Legacy;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace EventSourcingCqrs.Migration.Demo;

// Chapter 18: the demo's startup and composition root. It brings the two databases up to a runnable
// state (compose's healthcheck covers the container coming up, this covers the schema) and composes the
// event-sourced application side. The composition is textually parallel to CdcDatabaseFixture's block,
// so any drift between the demo and the tested shape shows up in a diff of the two.
public static class DemoStartup
{
    public static async Task PrepareDatabasesAsync(
        string legacyConnectionString, string eventStoreConnectionString)
    {
        await WaitForLegacyDatabaseAsync(legacyConnectionString);
        await EnsureEventStoreDatabaseAsync(legacyConnectionString, eventStoreConnectionString);
        await new MigrationRunner(
                EventStorePostgresMigrations.Assembly,
                EventStorePostgresMigrations.ResourcePrefix)
            .RunPendingAsync(
                new MigrationRunnerOptions { ConnectionString = eventStoreConnectionString },
                CancellationToken.None);
        await new LegacySchemaApplier().ApplyAsync(legacyConnectionString, CancellationToken.None);
    }

    // Textually parallel to CdcDatabaseFixture.CreateContextAsync's composition block: change one, change
    // the other, and the diff shows it.
    public static ServiceProvider Compose(string eventStoreConnectionString)
    {
        var services = new ServiceCollection();
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddSingleton<IEventTypeProvider, SalesEventTypeProvider>();
        services.AddPostgresEventStore(options => options.ConnectionString = eventStoreConnectionString);
        services.AddPostgresSnapshotStore(eventStoreConnectionString);
        services.AddPostgresIdempotencyStore(eventStoreConnectionString);
        services.AddApplication();
        return services.BuildServiceProvider();
    }

    // Bounded retry: compose's healthcheck gates the demo on postgres accepting connections, but the demo
    // must not crash on a startup race, so it retries the open before giving up.
    private static async Task WaitForLegacyDatabaseAsync(string connectionString)
    {
        const int maxAttempts = 30;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await using var connection = new NpgsqlConnection(connectionString);
                await connection.OpenAsync();
                return;
            }
            catch (NpgsqlException) when (attempt < maxAttempts)
            {
                Console.WriteLine($"Waiting for the legacy database (attempt {attempt}/{maxAttempts})...");
                await Task.Delay(TimeSpan.FromSeconds(1));
            }
        }
    }

    private static async Task EnsureEventStoreDatabaseAsync(
        string legacyConnectionString, string eventStoreConnectionString)
    {
        var eventStoreDatabase = new NpgsqlConnectionStringBuilder(eventStoreConnectionString).Database
            ?? throw new InvalidOperationException("EVENT_STORE_CONNECTION_STRING names no database.");

        await using var connection = new NpgsqlConnection(legacyConnectionString);
        await connection.OpenAsync();

        await using (var check = connection.CreateCommand())
        {
            check.CommandText = "SELECT 1 FROM pg_database WHERE datname = @name";
            check.Parameters.AddWithValue("name", eventStoreDatabase);
            if (await check.ExecuteScalarAsync() is not null)
            {
                return;
            }
        }

        // PostgreSQL has no CREATE DATABASE IF NOT EXISTS, and CREATE DATABASE takes no name parameter and
        // cannot run inside a transaction, so a check against pg_database then a plain create stands in.
        // No race to guard here: at startup the demo is the only writer.
        await using var create = connection.CreateCommand();
        create.CommandText = $"CREATE DATABASE \"{eventStoreDatabase}\"";
        await create.ExecuteNonQueryAsync();
    }
}

// The resolved services a scenario runs against, parallel to the tests' CdcTestContext.
public sealed record DemoContext(
    string LegacyConnectionString,
    IEventStore EventStore,
    ICurrentEventSchemaVersions SchemaVersions,
    EventTypeRegistry EventTypes,
    JsonSerializerOptions JsonOptions,
    ICommandBus CommandBus);
