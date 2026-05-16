using EventSourcingCqrs.Infrastructure.EventStore.Postgres;
using EventSourcingCqrs.Infrastructure.Migrations.Postgres;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

namespace EventSourcingCqrs.Infrastructure.Tests.Postgres;

// Shared PostgreSQL container for all tests in the class fixture. Each test
// asks for its own database via CreateDatabaseAsync so test state doesn't
// leak across cases; the container itself stays up for the class lifetime
// to amortize the startup cost (one ~3s startup beats five).
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

    // Convenience for adapter tests that need a freshly-created database
    // with 0001_initial_event_store.sql already applied. Migration-runner
    // tests stay on the bare CreateDatabaseAsync path because they exercise
    // the application itself.
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
