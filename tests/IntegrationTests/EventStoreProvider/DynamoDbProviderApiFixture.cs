using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.Infrastructure.EventStore.DynamoDb;
using EventSourcingCqrs.IntegrationTests.Authentication;
using EventSourcingCqrs.TestInfrastructure;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Options;
using Npgsql;
using Xunit;

namespace EventSourcingCqrs.IntegrationTests.EventStoreProvider;

// Boots the Api host with its event store on DynamoDB and its read models on PostgreSQL, the DynamoDB
// analog of KurrentProviderApiFixture and of PLAN.md:253: switching the configured event store is a
// configuration change, not a code change.
//
// Two containers, because the split is real. The provider key selects the engine that holds events;
// the read-model database stays PostgreSQL under its own key, holds the companion ports' tables and
// the Admin row the principal factory reads, and the Api host refuses to start without it. DynamoDB
// has no migration set; its one table is provisioned here, because the Api host registers no hosted
// services and the Workers host that owns provisioning is not in this composition. That is the same
// reason this fixture runs the PostgreSQL migrations itself.
//
// The table takes DynamoDbEventStoreOptions' default name rather than a per-fact one. The isolation
// unit here is the container, not the table: this fixture boots one host against one LocalStack, and
// the host's arm has no configuration key for a table name. Infrastructure.Tests' LocalStackFixture
// hands out a fresh table per fact because it shares one container across a class; nothing here does.
//
// LocalStack is pinned to 4.14.0 for the reason CLAUDE.md records: :latest exits 55 on a license check
// unless an auth token is set, and the community edition is archived rather than superseded. Built
// through the generic ContainerBuilder, which keeps this to the Testcontainers package the repository
// already carries.
public sealed class DynamoDbProviderApiFixture : IAsyncLifetime
{
    private readonly PostgresFixture _postgres = new();

    private readonly IContainer _localStack = new ContainerBuilder()
        .WithImage("localstack/localstack:4.14.0")
        .WithPortBinding(4566, true)
        .WithWaitStrategy(Wait.ForUnixContainer().UntilContainerIsHealthy())
        .Build();

    public WebApplicationFactory<Program> Factory { get; private set; } = null!;

    public string ServiceUrl { get; private set; } = null!;

    public string ReadModelConnectionString { get; private set; } = null!;

    private DynamoDbEventStoreOptions Options => new()
    {
        ServiceUrl = ServiceUrl,
        AccessKeyId = "test",
        SecretAccessKey = "test",
    };

    public async Task InitializeAsync()
    {
        await _postgres.InitializeAsync();
        ReadModelConnectionString = await _postgres.CreateMigratedDatabaseAsync();
        await SeedAdminRoleAsync(ForwardedIdentityTestHeader.DefaultActorId);

        await _localStack.StartAsync();
        ServiceUrl = $"http://{_localStack.Hostname}:{_localStack.GetMappedPublicPort(4566)}";

        using var client = CreateClient();
        await new DynamoDbTableProvisioner(client, Microsoft.Extensions.Options.Options.Create(Options))
            .EnsureTableAsync(CancellationToken.None);

        // The host resolves credentials through the SDK's own chain, which reads process environment
        // rather than IConfiguration, so UseSetting cannot reach it and this can. They are never
        // unset: the chain re-reads them per request, the variables are process-global across every
        // parallel collection, and a cleanup would pull them out from under an in-flight client.
        // Inert for every other test here, because nothing else in this assembly builds an AWS client.
        //
        // The values are distinctive on purpose. A developer box with ~/.aws/credentials would let a
        // fixture that silently failed to set these pass locally off the real profile and fail on CI,
        // so a value that could only have come from this line is the difference between a green run
        // and a green run that proves something.
        Environment.SetEnvironmentVariable("AWS_ACCESS_KEY_ID", "localstack-fixture-key");
        Environment.SetEnvironmentVariable("AWS_SECRET_ACCESS_KEY", "localstack-fixture-secret");

        // No EVENT_STORE_CONNECTION_STRING, and the absence is the fact. DynamoDB is addressed by its
        // own key, and this host does not ask for a DSN on this provider; setting one anyway would
        // make this fixture pass whether or not that conditional exists. No table-name key either:
        // the table provisioned above is the adapter's default, which is what the host falls back to.
        Factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("EVENT_STORE_PROVIDER", "DynamoDb");
            builder.UseSetting("EVENT_STORE_DYNAMODB_SERVICE_URL", ServiceUrl);
            builder.UseSetting("READ_MODEL_CONNECTION_STRING", ReadModelConnectionString);
            builder.UseSetting(
                "FORWARDED_IDENTITY_SIGNING_SECRET", ForwardedIdentityTestHeader.SigningSecret);
        });
    }

    public async Task DisposeAsync()
    {
        await Factory.DisposeAsync();
        await _localStack.DisposeAsync();
        await _postgres.DisposeAsync();
    }

    private AmazonDynamoDBClient CreateClient() => new(
        "test",
        "test",
        new AmazonDynamoDBConfig { ServiceURL = ServiceUrl, AuthenticationRegion = "us-east-1" });

    // Reads one stream's event types straight out of DynamoDB via a bare client. The assertion has to
    // bypass IEventStore: resolving that port from the host would prove only that the container hands
    // back whatever adapter it composed, never which engine the events reached.
    //
    // The attribute names are literals rather than DynamoDbSchema's constants, because that type is
    // internal to the adapter and this assembly holds no InternalsVisibleTo grant for it. Spelling the
    // physical layout out here is the point rather than a cost: a read through the adapter's own
    // vocabulary would move with the adapter, and this fact exists to check the bytes on the engine.
    public async Task<IReadOnlyList<string>> ReadEventTypesAsync(string streamId)
    {
        using var client = CreateClient();
        var response = await client.QueryAsync(new QueryRequest
        {
            TableName = Options.TableName,
            KeyConditionExpression = "pk = :pk",
            ExpressionAttributeValues = new() { [":pk"] = new AttributeValue { S = streamId } },
            ConsistentRead = true,
            ScanIndexForward = true,
        });

        return [.. response.Items.Select(item => item["event_type"].S)];
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
