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
}
