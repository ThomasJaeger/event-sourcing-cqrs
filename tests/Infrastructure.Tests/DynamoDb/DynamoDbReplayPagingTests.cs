using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.Runtime;
using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.EventStore.ContractTests;
using FluentAssertions;
using Xunit;

namespace EventSourcingCqrs.Infrastructure.Tests.DynamoDb;

public sealed class DynamoDbReplayPagingTests(LocalStackFixture fixture)
    : IClassFixture<LocalStackFixture>
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Replay_yields_the_first_page_before_reading_the_rest(bool tenantOnly)
    {
        await using var backend = await DynamoDbContractBackend.CreateAsync(fixture);
        var stream = ContractEnvelopes.NewStreamId();
        // A real Query page is limited to 1 MiB. This batch crosses that boundary while
        // remaining below the transaction and individual-item size limits.
        var events = Enumerable.Range(1, 12)
            .Select(version => ContractEnvelopes.Build(
                stream, version, new ContractOrderNoted(new string('x', 120_000))))
            .ToArray();
        await backend.Store.AppendAsync(stream, 0, events, CancellationToken.None);
        using var observed = new QueryObservingClient(backend.Client);
        var store = backend.CreateStore(observed);
        var feed = tenantOnly
            ? store.ReadAllForTenantAsync(WellKnownTenants.Default, 0, 12)
            : store.ReadAllAsync(0);

        await using var reader = feed.GetAsyncEnumerator();
        (await reader.MoveNextAsync()).Should().BeTrue();
        observed.QueryCount.Should().Be(1,
            "a replay consumer must receive the first page before the remaining backlog is fetched");

        var positions = new List<long> { reader.Current.GlobalPosition };
        while (await reader.MoveNextAsync())
            positions.Add(reader.Current.GlobalPosition);

        positions.Should().Equal(Enumerable.Range(1, 12).Select(p => (long)p));
        observed.QueryCount.Should().BeGreaterThan(1,
            "the real backlog must span multiple pages for the streaming assertion to have teeth");
    }

    // Like RecordingStreamsDecorator, this observes the existing injected SDK seam.
    // Every query reaches LocalStack unchanged; no response is invented or replaced.
    private sealed class QueryObservingClient(IAmazonDynamoDB inner)
        : AmazonDynamoDBClient(
            new BasicAWSCredentials("recording", "recording"),
            new AmazonDynamoDBConfig
            {
                ServiceURL = "http://localhost:1",
                AuthenticationRegion = "us-east-1",
            })
    {
        public int QueryCount { get; private set; }

        public override async Task<QueryResponse> QueryAsync(
            QueryRequest request, CancellationToken cancellationToken = default)
        {
            var page = await inner.QueryAsync(request, cancellationToken);
            QueryCount++;
            return page;
        }
    }
}
