namespace EventSourcingCqrs.Domain.Abstractions;

// Process-manager stream-id prefixes per ADR 0011. Aggregate stream prefixes
// are derived from the aggregate type name inside StreamId.ForAggregate, so
// only the PM prefixes need constants here. ForProcessManager takes the prefix
// as an argument; these constants are the values callers pass.
public static class StreamPrefixes
{
    // The family prefix every process-manager stream carries. Read paths route on it to tell a PM stream
    // from an aggregate one: the two PM read guards, the Event Store Browser's pre-detection, the
    // idempotency-key guard, and the Correlation-ID Tracer's per-row classification. The full prefixes
    // below derive from it, so the family test and the values it tests cannot drift apart.
    //
    // The event store's SQL filters carry the same prefix as a literal ('pm-%'), which C# cannot share with
    // a const. StreamPrefixesTests pins the invariant those literals depend on.
    public const string ProcessManagerPrefix = "pm-";

    public const string OrderFulfillmentPm = ProcessManagerPrefix + "order-fulfillment";
    public const string ReturnPm = ProcessManagerPrefix + "return";
}
