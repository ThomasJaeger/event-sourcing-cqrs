using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Xunit;

namespace EventSourcingCqrs.Infrastructure.Tests.DynamoDb;

// LocalStack backing DynamoDB for the contract suite. One container per class, handing out a
// fresh TABLE per fact, which is the relational fixtures' shape rather than KurrentDB's.
//
// The isolation unit is the table. KurrentFixture hands out a fresh container per fact because
// KurrentDB's $all feed is node-global and three core facts assert over the whole feed, so
// nothing smaller than a node isolates them. DynamoDB has no node-global feed: this adapter's
// global ordering is a log partition inside one table, the position counter is a row inside that
// same table, and every read the adapter issues names its own table. Two tables in one LocalStack
// share nothing, so a fresh table name gives a fresh feed and a fresh counter, exactly as a fresh
// PostgreSQL database does. That buys back the roughly 5.6s per fact Kurrent pays.
//
// Pinned to 4.14.0, and the pin is load-bearing. localstack/localstack:latest is 2026.6.3, which
// exits 55 on startup with "License activation failed" unless LOCALSTACK_AUTH_TOKEN is set; the
// tag list carries a community-archive tag, so the community edition is archived rather than
// merely superseded. 4.14.0 starts with no token and reports dynamodb and dynamodbstreams
// available. Built through the generic ContainerBuilder rather than a Testcontainers.LocalStack
// module, which keeps this to the Testcontainers package the repository already carries.
public sealed class LocalStackFixture : IAsyncLifetime
{
    private readonly IContainer _container = new ContainerBuilder("localstack/localstack:4.14.0")
        .WithPortBinding(4566, true)
        .WithWaitStrategy(Wait.ForUnixContainer().UntilContainerIsHealthy())
        .Build();

    public Task InitializeAsync() => _container.StartAsync();

    public Task DisposeAsync() => _container.DisposeAsync().AsTask();

    public string ServiceUrl =>
        $"http://{_container.Hostname}:{_container.GetMappedPublicPort(4566)}";

    // A table name no other fact uses. The fact's whole world is this table.
    public static string NewTableName() => "events_" + Guid.NewGuid().ToString("N");
}
