namespace EventSourcingCqrs.Infrastructure.EventStore.DynamoDb;

// Knobs for the Streams dispatch service. Every one of them paces a loop; none of them changes what
// the loop means.
public sealed class DynamoDbStreamDispatchOptions
{
    // How long a fault waits before the shard walk starts over. Mirrors the KurrentDB subscription's
    // reconnect back-off.
    public TimeSpan ReconnectBackoff { get; set; } = TimeSpan.FromSeconds(2);

    // How long the shard walk waits when GetRecords returns nothing. A DynamoDB Streams shard
    // iterator returns empty pages between writes rather than blocking, so something has to decide
    // how eagerly to ask again.
    //
    // This is not the projection trigger. PLAN.md:475 asks that Streams feed projections "without
    // polling", and what that forbids is polling the event table for new events, the way the
    // relational adapters' outbox processors do. The change feed is still the trigger. On a quiet
    // feed this interval costs one GetRecords per live shard and nothing else: no DescribeStream
    // (that sits on the refresh interval below) and no read of the event table, which is the half
    // that matters.
    public TimeSpan EmptyShardBackoff { get; set; } = TimeSpan.FromMilliseconds(250);

    // How often the walk re-lists shards to find splits. Shard discovery is a DescribeStream, and
    // running one per wake would put a control-plane call on the hot path for news that arrives
    // maybe once a day. The walk refreshes on this interval, and out of band on the events that
    // can invalidate its shard set: start, fault restart, iterator expiry, and a shard closing.
    //
    // A split's child shard is therefore found late, by up to this interval. That costs latency on
    // the child's first records and loses nothing: a newly acquired iterator always forces a drain,
    // and the drain reads the log rather than the stream.
    public TimeSpan ShardRefreshInterval { get; set; } = TimeSpan.FromSeconds(30);
}
