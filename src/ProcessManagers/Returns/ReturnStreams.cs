using EventSourcingCqrs.Domain.Abstractions;

namespace EventSourcingCqrs.ProcessManagers.Returns;

// The Return PM's stream identity and rehydration factory, parallel to
// OrderFulfillmentStreams. The stream is pm-return:{orderId:N} for the default tenant
// and pm-return:{tenant:N}:{orderId:N} for any other, per ADR 0011, keyed by OrderId
// (the returned shipment's order); the factory is the construction path the
// repository replays onto (ADR 0012).
internal static class ReturnStreams
{
    public static StreamId For(TenantId tenant, Guid orderId) =>
        StreamId.ForProcessManager(StreamPrefixes.ReturnPm, tenant, orderId);

    public static ReturnProcessManager New(StreamId streamId) => new(streamId);
}
