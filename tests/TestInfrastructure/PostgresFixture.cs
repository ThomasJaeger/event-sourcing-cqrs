using EventSourcingCqrs.Infrastructure.EventStore.Postgres;
using EventSourcingCqrs.Infrastructure.Migrations.Postgres;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

namespace EventSourcingCqrs.TestInfrastructure;

// Shared PostgreSQL container for every test project that needs a real
// PostgreSQL backend. Each test asks for its own database via
// CreateDatabaseAsync so test state doesn't leak across cases; the
// container itself stays up for the class lifetime (per
// IClassFixture<PostgresFixture>) to amortize the startup cost.
//
// xUnit fixture scoping is per-assembly, so each test assembly that uses
// this fixture spins up its own container at the first IClassFixture
// resolution. Three assemblies use it as of Session 0006: Infrastructure
// .Tests, Projections.Tests, Workers.Tests. Containers do not cross
// assembly boundaries.
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

    // Convenience for tests that need a freshly-created database with every
    // migration applied. MigrationRunner is resource-driven through
    // EventStorePostgresMigrations, so this picks up every migration in
    // event_store and read_models with no variant.
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
