using EventSourcingCqrs.Domain.Abstractions;

namespace EventSourcingCqrs.ProcessManagers.OrderFulfillment;

// The PM's stream identity and rehydration factory, shared by the PM handler and
// the timeout command handlers. The stream is pm-order-fulfillment:{orderId:N} for
// the default tenant and pm-order-fulfillment:{tenant:N}:{orderId:N} for any other,
// per ADR 0011; the factory is the construction path the repository replays onto
// (ADR 0012, no parameterless constructor).
internal static class OrderFulfillmentStreams
{
    public static StreamId For(TenantId tenant, Guid orderId) =>
        StreamId.ForProcessManager(StreamPrefixes.OrderFulfillmentPm, tenant, orderId);

    public static OrderFulfillmentProcessManager New(StreamId streamId) => new(streamId);
}
