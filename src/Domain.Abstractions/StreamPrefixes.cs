namespace EventSourcingCqrs.Domain.Abstractions;

// Process-manager stream-id prefixes per ADR 0011. Aggregate stream prefixes
// are derived from the aggregate type name inside StreamId.ForAggregate, so
// only the PM prefixes need constants here. ForProcessManager takes the prefix
// as an argument; these constants are the values callers pass.
public static class StreamPrefixes
{
    public const string OrderFulfillmentPm = "pm-order-fulfillment";
    public const string ReturnPm = "pm-return";
}
