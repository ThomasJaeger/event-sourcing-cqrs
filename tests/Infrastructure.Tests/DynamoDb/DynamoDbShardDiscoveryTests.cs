using EventSourcingCqrs.Infrastructure.EventStore.DynamoDb;
using FluentAssertions;
using Xunit;

namespace EventSourcingCqrs.Infrastructure.Tests.DynamoDb;

public sealed class DynamoDbShardDiscoveryTests
{
    [Fact]
    public async Task Discovery_follows_every_continuation_even_when_a_page_is_empty()
    {
        // LocalStack cannot deterministically split a table into more than 100 shards.
        // This pins our page-walking policy through an owned internal delegate, not an
        // SDK stand-in. The existing live dispatch facts exercise the SDK mapping.
        var requested = new List<string?>();
        Task<DynamoDbShardPage> ReadPage(string? after, CancellationToken ct)
        {
            requested.Add(after);
            return Task.FromResult(after switch
            {
                null => new DynamoDbShardPage(["parent"], "page-one"),
                "page-one" => new DynamoDbShardPage([], "page-two"),
                "page-two" => new DynamoDbShardPage(["child-a", "child-b"], null),
                _ => throw new InvalidOperationException($"Unexpected continuation {after}."),
            });
        }

        var shards = new List<string>();
        await foreach (var shard in DynamoDbShardDiscovery.ReadAllAsync(ReadPage))
            shards.Add(shard);

        shards.Should().Equal("parent", "child-a", "child-b");
        requested.Should().Equal(new string?[] { null, "page-one", "page-two" });
    }
}
