using EventSourcingCqrs.Infrastructure.EventStore.Postgres;
using EventSourcingCqrs.Infrastructure.Migrations.Postgres;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

namespace EventSourcingCqrs.Projections.Tests;

// Shared PostgreSQL container for all tests in the class fixture. Each test
// asks for its own database via CreateDatabaseAsync so test state doesn't
// leak across cases; the container itself stays up for the class lifetime
// to amortize the startup cost.
//
// Duplicated from Infrastructure.Tests per the Session 0005 setup document.
// A third consumer triggers extraction to a shared test-infrastructure project.
public sealed class PostgresFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder()
        .WithImage("postgres:16.6-alpine")
        .WithUsername("esrcq")
        .WithPassword("esrcq")
        .WithDatabase("esrcq")
        .Build();

    public Task InitializeAsync() => _container.StartAsync();

    public Task DisposeAsync() => _container.DisposeAsync().AsTask();

    public async Task<string> CreateDatabaseAsync()
    {
        var dbName = "test_" + Guid.NewGuid().ToString("N");
        await using var connection = new NpgsqlConnection(_container.GetConnectionString());
        await connection.OpenAsync();
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"CREATE DATABASE \"{dbName}\"";
        await cmd.ExecuteNonQueryAsync();

        var builder = new NpgsqlConnectionStringBuilder(_container.GetConnectionString())
        {
            Database = dbName,
        };
        return builder.ConnectionString;
    }

    // A freshly-created database with every migration applied. MigrationRunner
    // is resource-driven, so this picks up 0001 through 0003 with no variant.
    public async Task<string> CreateMigratedDatabaseAsync()
    {
        var connectionString = await CreateDatabaseAsync();
        await new MigrationRunner(
                EventStorePostgresMigrations.Assembly,
                EventStorePostgresMigrations.ResourcePrefix)
            .RunPendingAsync(
                new MigrationRunnerOptions { ConnectionString = connectionString },
                CancellationToken.None);
        return connectionString;
    }
}
