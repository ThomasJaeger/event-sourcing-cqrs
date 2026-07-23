using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.Domain.Sales;
using EventSourcingCqrs.Infrastructure.EventStore.Postgres;
using EventSourcingCqrs.Infrastructure.Migrations.Postgres;
using EventSourcingCqrs.Migration.Demo.Legacy;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

namespace EventSourcingCqrs.Migration.Tests;

// A PostgreSQL container carrying both sides the CDC reader bridges. Per test it provisions a fresh
// legacy database (the CRUD schema, applied by the production LegacySchemaApplier as in S1) and a
// fresh event-store database (migrated by the real MigrationRunner with the embedded Postgres set),
// then composes a PostgresEventStore through the same registry the hosts use so the reader's appends
// and the tests' reads agree on the event type tokens. Fresh databases per test keep the reader's
// checkpoint and the seeded rows from leaking between facts.
//
// Siblings the S1 LegacyDatabaseFixture rather than extending it: S1's single-database shape needs no
// event store, and keeping the two apart leaves that fixture untouched. The container is shared for
// the class lifetime (IClassFixture) to amortize startup; the databases are per test.
public sealed class CdcDatabaseFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder()
        .WithImage("postgres:16.6-alpine")
        .WithUsername("esrcq")
        .WithPassword("esrcq")
        .WithDatabase("esrcq")
        .Build();

    private readonly List<ServiceProvider> _providers = [];

    public Task InitializeAsync() => _container.StartAsync();

    public async Task DisposeAsync()
    {
        foreach (var provider in _providers)
        {
            await provider.DisposeAsync();
        }

        await _container.DisposeAsync();
    }

    // A fresh legacy database and event-store database, plus a composed event store over the latter.
    public async Task<CdcTestContext> CreateContextAsync()
    {
        var legacyConnectionString = await CreateDatabaseAsync("legacy_" + Guid.NewGuid().ToString("N"));
        await new LegacySchemaApplier().ApplyAsync(legacyConnectionString, CancellationToken.None);

        var eventStoreConnectionString = await CreateDatabaseAsync("es_" + Guid.NewGuid().ToString("N"));
        await new MigrationRunner(
                EventStorePostgresMigrations.Assembly,
                EventStorePostgresMigrations.ResourcePrefix)
            .RunPendingAsync(
                new MigrationRunnerOptions { ConnectionString = eventStoreConnectionString },
                CancellationToken.None);

        var services = new ServiceCollection();
        services.AddSingleton<IEventTypeProvider, SalesEventTypeProvider>();
        services.AddPostgresEventStore(options => options.ConnectionString = eventStoreConnectionString);
        var provider = services.BuildServiceProvider();
        _providers.Add(provider);

        return new CdcTestContext(
            legacyConnectionString,
            provider.GetRequiredService<IEventStore>(),
            provider.GetRequiredService<ICurrentEventSchemaVersions>());
    }

    private async Task<string> CreateDatabaseAsync(string databaseName)
    {
        await using var connection = new NpgsqlConnection(_container.GetConnectionString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"CREATE DATABASE \"{databaseName}\"";
        await command.ExecuteNonQueryAsync();

        return new NpgsqlConnectionStringBuilder(_container.GetConnectionString())
        {
            Database = databaseName,
        }.ConnectionString;
    }
}

public sealed record CdcTestContext(
    string LegacyConnectionString,
    IEventStore EventStore,
    ICurrentEventSchemaVersions SchemaVersions);
