using System.Diagnostics;
using EventSourcingCqrs.Infrastructure.EventStore.SqlServer;
using Microsoft.Data.SqlClient;
using Testcontainers.MsSql;
using Xunit;

namespace EventSourcingCqrs.Infrastructure.Tests.SqlServer;

// Shared SQL Server container for the adapter's tests. Each test asks for its own database so
// state does not leak across cases; the container stays up for the class lifetime to amortize
// the start cost.
//
// It lives here rather than in TestInfrastructure, which project-references the PostgreSQL
// adapter and its migration runner and is Postgres-coupled at the csproj level. Per ADR 0004
// the adapters share no test scaffolding either.
//
// Image: 2019, the version floor. UTF-8 collations arrived in 2019 and the schema depends on
// one, so 2019 is what CI has to prove; 2022 is the primary target and the matrix ruling comes
// later against measured runtime.
//
// Every test database gets READ_COMMITTED_SNAPSHOT ON. That is the Azure SQL default posture,
// and it is the posture in which the commit-visibility hazard is real: under SQL Server's
// default lock-based READ COMMITTED a reader BLOCKS on an uncommitted row instead of reading
// past it, which would hide the ADR 0044 skip hazard rather than exercise it. Testing under the
// safer-looking default would prove nothing.
public sealed class SqlServerFixture : IAsyncLifetime
{
    private readonly MsSqlContainer _container = new MsSqlBuilder()
        .WithImage("mcr.microsoft.com/mssql/server:2019-latest")
        .Build();

    // Measured once at container start, surfaced for the CI-matrix decision.
    public TimeSpan ContainerStartupTime { get; private set; }

    public async Task InitializeAsync()
    {
        var sw = Stopwatch.StartNew();
        await _container.StartAsync();
        sw.Stop();
        ContainerStartupTime = sw.Elapsed;
    }

    public Task DisposeAsync() => _container.DisposeAsync().AsTask();

    public async Task<string> CreateDatabaseAsync()
    {
        var dbName = "test_" + Guid.NewGuid().ToString("N");
        await using var connection = new SqlConnection(_container.GetConnectionString());
        await connection.OpenAsync();

        await using (var create = new SqlCommand($"CREATE DATABASE [{dbName}];", connection))
        {
            await create.ExecuteNonQueryAsync();
        }

        // RCSI on the fresh database. No other connection holds it yet, so ROLLBACK IMMEDIATE
        // has nothing to roll back; it is there so the statement cannot hang on a stray session.
        await using (var rcsi = new SqlCommand(
            $"ALTER DATABASE [{dbName}] SET READ_COMMITTED_SNAPSHOT ON WITH ROLLBACK IMMEDIATE;",
            connection))
        {
            await rcsi.ExecuteNonQueryAsync();
        }

        return new SqlConnectionStringBuilder(_container.GetConnectionString())
        {
            InitialCatalog = dbName,
        }.ConnectionString;
    }

    public async Task<string> CreateMigratedDatabaseAsync()
    {
        var connectionString = await CreateDatabaseAsync();
        await new SqlServerMigrationRunner(
                EventStoreSqlServerMigrations.Assembly,
                EventStoreSqlServerMigrations.ResourcePrefix)
            .RunPendingAsync(
                new SqlServerMigrationRunnerOptions { ConnectionString = connectionString },
                CancellationToken.None);
        return connectionString;
    }
}
