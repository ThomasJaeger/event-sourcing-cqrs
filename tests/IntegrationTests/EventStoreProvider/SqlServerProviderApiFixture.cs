using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.Infrastructure.EventStore.SqlServer;
using EventSourcingCqrs.IntegrationTests.Authentication;
using EventSourcingCqrs.TestInfrastructure;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.SqlClient;
using Npgsql;
using Testcontainers.MsSql;
using Xunit;

namespace EventSourcingCqrs.IntegrationTests.EventStoreProvider;

// Boots the Api host with its event store on SQL Server and its read models on PostgreSQL, the
// composition PLAN.md's Phase 2 provider-switch done-when asks for: switching the configured event store is a configuration change,
// not a code change.
//
// Two containers, because the split is real. The provider key selects the engine that holds events,
// outbox, command idempotency, and delayed commands; the read-model database stays PostgreSQL under
// its own key, and the Api host refuses to start without one. The Admin row the principal factory
// reads lives on the PostgreSQL side, so an authenticated command can still authorize.
//
// The fixture migrates both databases itself. The Api host runs no migrations by design, and the
// Workers host that owns migration orchestration is not in this composition, so nothing else would
// create either schema.
//
// The SQL Server container mirrors Infrastructure.Tests' SqlServerFixture rather than reusing it.
// That fixture is a peer in another test assembly, and its own header argues against promoting it
// to TestInfrastructure, which is Postgres-coupled at the csproj level. The 2019 image is the
// version floor and the test database gets READ_COMMITTED_SNAPSHOT ON, the Azure SQL default and
// the posture the adapter is written to hold under.
public sealed class SqlServerProviderApiFixture : IAsyncLifetime
{
    private readonly PostgresFixture _postgres = new();

    private readonly MsSqlContainer _sqlServer = new MsSqlBuilder()
        .WithImage("mcr.microsoft.com/mssql/server:2019-latest")
        .Build();

    public WebApplicationFactory<Program> Factory { get; private set; } = null!;

    public string EventStoreConnectionString { get; private set; } = null!;

    public string ReadModelConnectionString { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        await _postgres.InitializeAsync();
        ReadModelConnectionString = await _postgres.CreateMigratedDatabaseAsync();
        await SeedAdminRoleAsync(ForwardedIdentityTestHeader.DefaultActorId);

        await _sqlServer.StartAsync();
        EventStoreConnectionString = await CreateMigratedSqlServerDatabaseAsync();

        Factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("EVENT_STORE_PROVIDER", "SqlServer");
            builder.UseSetting("EVENT_STORE_CONNECTION_STRING", EventStoreConnectionString);
            builder.UseSetting("READ_MODEL_CONNECTION_STRING", ReadModelConnectionString);
            builder.UseSetting(
                "FORWARDED_IDENTITY_SIGNING_SECRET", ForwardedIdentityTestHeader.SigningSecret);
        });
    }

    public async Task DisposeAsync()
    {
        await Factory.DisposeAsync();
        await _sqlServer.DisposeAsync();
        await _postgres.DisposeAsync();
    }

    // Reads one stream's event types straight out of the SQL Server events table. The assertion has
    // to bypass IEventStore: resolving that port from the host would prove only that the container
    // hands back whatever adapter it composed, never which database the events reached.
    public async Task<IReadOnlyList<string>> ReadEventTypesAsync(string streamId)
    {
        await using var connection = new SqlConnection(EventStoreConnectionString);
        await connection.OpenAsync();
        await using var cmd = connection.CreateCommand();
        cmd.CommandText =
            "SELECT event_type FROM event_store.events WHERE stream_id = @stream_id " +
            "ORDER BY stream_version";
        cmd.Parameters.AddWithValue("stream_id", streamId);
        var eventTypes = new List<string>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            eventTypes.Add(reader.GetString(0));
        }
        return eventTypes;
    }

    // The same read against the PostgreSQL read-model database. That database carries an event_store
    // schema of its own, because the PostgreSQL migration set is not split by schema, so the table
    // this reads exists and is the one a host composed on the wrong adapter would have written to.
    public async Task<IReadOnlyList<string>> ReadPostgresEventTypesAsync(string streamId)
    {
        await using var connection = new NpgsqlConnection(ReadModelConnectionString);
        await connection.OpenAsync();
        await using var cmd = connection.CreateCommand();
        cmd.CommandText =
            "SELECT event_type FROM event_store.events WHERE stream_id = @stream_id "
            + "ORDER BY stream_version";
        cmd.Parameters.AddWithValue("stream_id", streamId);
        var eventTypes = new List<string>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            eventTypes.Add(reader.GetString(0));
        }
        return eventTypes;
    }

    // The same direct current-roles insert ApiFixture makes, against the read-model database rather
    // than against one database serving both roles. Admin holds every permission, so the command
    // this fixture's tests dispatch authorizes.
    private async Task SeedAdminRoleAsync(Guid userId)
    {
        await using var connection = new NpgsqlConnection(ReadModelConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            "INSERT INTO read_models.current_user_roles (user_id, role) " +
            "VALUES (@user_id, @role) ON CONFLICT (user_id, role) DO NOTHING";
        command.Parameters.AddWithValue("user_id", userId);
        command.Parameters.AddWithValue("role", Role.Admin.ToString());
        await command.ExecuteNonQueryAsync();
    }

    private async Task<string> CreateMigratedSqlServerDatabaseAsync()
    {
        var dbName = "test_" + Guid.NewGuid().ToString("N");
        await using (var connection = new SqlConnection(_sqlServer.GetConnectionString()))
        {
            await connection.OpenAsync();

            await using (var create = new SqlCommand($"CREATE DATABASE [{dbName}];", connection))
            {
                await create.ExecuteNonQueryAsync();
            }

            await using var rcsi = new SqlCommand(
                $"ALTER DATABASE [{dbName}] SET READ_COMMITTED_SNAPSHOT ON WITH ROLLBACK IMMEDIATE;",
                connection);
            await rcsi.ExecuteNonQueryAsync();
        }

        var connectionString = new SqlConnectionStringBuilder(_sqlServer.GetConnectionString())
        {
            InitialCatalog = dbName,
        }.ConnectionString;

        await new SqlServerMigrationRunner(
                EventStoreSqlServerMigrations.Assembly,
                EventStoreSqlServerMigrations.ResourcePrefix)
            .RunPendingAsync(
                new SqlServerMigrationRunnerOptions { ConnectionString = connectionString },
                CancellationToken.None);
        return connectionString;
    }
}
