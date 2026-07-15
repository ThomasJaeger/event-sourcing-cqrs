using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.IntegrationTests.Authentication;
using EventSourcingCqrs.TestInfrastructure;
using KurrentDB.Client;
using Microsoft.AspNetCore.Mvc.Testing;
using Npgsql;
using Xunit;

namespace EventSourcingCqrs.IntegrationTests.EventStoreProvider;

// Boots the Api host with its event store on KurrentDB and its read models on PostgreSQL, the Kurrent
// analog of SqlServerProviderApiFixture and of PLAN.md:455: switching the configured event store to
// KurrentDB is a configuration change, not a code change.
//
// Two containers, because the split is real. The provider key selects the engine that holds events;
// the read-model database stays PostgreSQL under its own key, holds the companion ports' tables and
// the Admin row the principal factory reads, and the Api host refuses to start without it. KurrentDB
// has no schema to migrate; the read-model database gets the full migration set from this fixture,
// because the Api host runs none and the Workers host that owns migration is not in this composition.
//
// KurrentDB runs insecure and mem-db, the spike's mode: no on-disk state and no TLS for a throwaway
// node the host reaches over a mapped port with an esdb:// tls=false string.
public sealed class KurrentProviderApiFixture : IAsyncLifetime
{
    private readonly PostgresFixture _postgres = new();

    private readonly IContainer _kurrent = new ContainerBuilder()
        .WithImage("kurrentplatform/kurrentdb:26.0.2")
        .WithCommand("--insecure", "--mem-db")
        .WithPortBinding(2113, true)
        .WithWaitStrategy(Wait.ForUnixContainer().UntilContainerIsHealthy())
        .Build();

    public WebApplicationFactory<Program> Factory { get; private set; } = null!;

    public string EventStoreConnectionString { get; private set; } = null!;

    public string ReadModelConnectionString { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        await _postgres.InitializeAsync();
        ReadModelConnectionString = await _postgres.CreateMigratedDatabaseAsync();
        await SeedAdminRoleAsync(ForwardedIdentityTestHeader.DefaultActorId);

        await _kurrent.StartAsync();
        EventStoreConnectionString =
            $"esdb://{_kurrent.Hostname}:{_kurrent.GetMappedPublicPort(2113)}?tls=false";

        Factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("EVENT_STORE_PROVIDER", "Kurrent");
            builder.UseSetting("EVENT_STORE_CONNECTION_STRING", EventStoreConnectionString);
            builder.UseSetting("READ_MODEL_CONNECTION_STRING", ReadModelConnectionString);
            builder.UseSetting(
                "FORWARDED_IDENTITY_SIGNING_SECRET", ForwardedIdentityTestHeader.SigningSecret);
        });
    }

    public async Task DisposeAsync()
    {
        await Factory.DisposeAsync();
        await _kurrent.DisposeAsync();
        await _postgres.DisposeAsync();
    }

    // Reads one stream's event types straight out of KurrentDB via a bare client. The assertion has to
    // bypass IEventStore: resolving that port from the host would prove only that the container hands
    // back whatever adapter it composed, never which engine the events reached.
    public async Task<IReadOnlyList<string>> ReadEventTypesAsync(string streamId)
    {
        var settings = KurrentDBClientSettings.Create(EventStoreConnectionString);
        await using var client = new KurrentDBClient(settings);

        var read = client.ReadStreamAsync(Direction.Forwards, streamId, StreamPosition.Start);
        if (await read.ReadState == ReadState.StreamNotFound)
        {
            return [];
        }

        var eventTypes = new List<string>();
        await foreach (var resolved in read)
        {
            eventTypes.Add(resolved.Event.EventType);
        }
        return eventTypes;
    }

    // The same read against the PostgreSQL read-model database. That database carries an event_store
    // schema of its own, because the PostgreSQL migration set is not split by schema, so the table this
    // reads exists and is the one a host composed on the wrong adapter would have written to.
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
}
