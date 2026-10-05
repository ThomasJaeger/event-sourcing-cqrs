using System.Runtime.CompilerServices;

namespace EventSourcingCqrs.Infrastructure.EventStore.DynamoDb;

// Our page-walking policy, separate from the SDK call so pagination can be pinned without
// inventing engine responses or requiring a table with hundreds of live shards.
internal static class DynamoDbShardDiscovery
{
    public static async IAsyncEnumerable<string> ReadAllAsync(
        Func<string?, CancellationToken, Task<DynamoDbShardPage>> readPage,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        string? after = null;
        do
        {
            ct.ThrowIfCancellationRequested();
            var page = await readPage(after, ct);
            foreach (var shard in page.ShardIds)
            {
                ct.ThrowIfCancellationRequested();
                yield return shard;
            }
            after = page.Continuation;
        }
        while (!string.IsNullOrEmpty(after));
    }
}

internal sealed record DynamoDbShardPage(IReadOnlyList<string> ShardIds, string? Continuation);
