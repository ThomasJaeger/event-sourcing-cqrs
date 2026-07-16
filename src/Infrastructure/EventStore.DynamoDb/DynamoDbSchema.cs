namespace EventSourcingCqrs.Infrastructure.EventStore.DynamoDb;

// The events table's shape, and the transaction layout the append writes into it.
//
// Reserved partition keys. Aggregate and PM rows key on their stream id, so these two literals
// have to be shapes no stream id can take: StreamId is "{prefix}:{guid:N}" or
// "{prefix}:{tenant:N}:{guid:N}" (ADR 0011), and a leading '$' cannot appear in a prefix that
// parses.
//
// The log partition is where global ordering lives. DynamoDB has no engine-assigned global
// sequence, and a GSI cannot serve a strongly-consistent read, so an append writes one log row per
// event under a single partition key with the position as its sort key. Reading that partition
// with ConsistentRead in sort order is the global feed, and it is the only read on this engine
// that can hold ADR 0044's commit-order visibility. ADR 0049 records why the GSI PLAN.md:463
// proposed does not work.
internal static class DynamoDbSchema
{
    public const string PartitionKeyAttribute = "pk";
    public const string SortKeyAttribute = "sk";

    // The single row holding the last issued global position. Every append conditions on its
    // current value, which is what serializes position assignment.
    public const string CounterPartitionKey = "$counter";
    public const string CounterValueAttribute = "current";

    // The global feed: one row per committed event, sort key = global position.
    public const string LogPartitionKey = "$log";

    // One row per event id, existence-checked. The engine has no opinion about a reused event id,
    // so this row is what gives the adapter one.
    public const string EventIdPartitionKeyPrefix = "$eid#";

    // Row attributes. The log row carries the whole envelope, so ReadAllAsync is one Query rather
    // than a fan-out of per-stream reads.
    public const string StreamIdAttribute = "stream_id";
    public const string VersionAttribute = "version";
    public const string EventIdAttribute = "event_id";
    public const string EventTypeAttribute = "event_type";
    public const string EventVersionAttribute = "event_version";
    public const string PayloadAttribute = "payload";
    public const string MetadataAttribute = "metadata";
    public const string OccurredUtcAttribute = "occurred_utc";
    public const string PositionAttribute = "position";

    // TransactWriteItems' 100-item ceiling, measured against the live engine: 101 items are
    // rejected with "Member must have length less than or equal to 100" (ValidationException).
    public const int TransactionItemLimit = 100;

    // Items per event: the event row, the log row, the event-id row.
    public const int ItemsPerEvent = 3;

    // THE ITEM-ORDERING INVARIANT. Cancellation reasons come back positionally, one per
    // transaction item, so translating a failure into the right exception depends entirely on
    // knowing which index means what. An append of n events lays its items out as:
    //
    //   index 0            the counter update      condition: current = P
    //   index 1 .. n       the event rows          condition: attribute_not_exists(sk)
    //   index n+1 .. 2n    the log rows            condition: attribute_not_exists(sk)
    //   index 2n+1 .. 3n   the event-id rows       condition: attribute_not_exists(pk)
    //
    // Change this layout and the translation in DynamoDbEventStore.TranslateCancellation goes
    // silently wrong: a version conflict would surface as contention and retry forever, or a
    // duplicate event id would arrive dressed as ConcurrencyException. The two live together for
    // that reason, and the helpers below are the only place the arithmetic is written down.
    public static int CounterIndex => 0;

    public static bool IsEventRowIndex(int index, int eventCount)
        => index >= 1 && index <= eventCount;

    public static bool IsLogRowIndex(int index, int eventCount)
        => index >= eventCount + 1 && index <= 2 * eventCount;

    public static bool IsEventIdRowIndex(int index, int eventCount)
        => index >= 2 * eventCount + 1 && index <= 3 * eventCount;
}
