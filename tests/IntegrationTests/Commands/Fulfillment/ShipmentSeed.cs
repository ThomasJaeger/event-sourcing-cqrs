using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.Domain.Fulfillment;
using EventSourcingCqrs.Domain.Fulfillment.Events;
using EventSourcingCqrs.Domain.SharedKernel;

namespace EventSourcingCqrs.IntegrationTests.Commands.Fulfillment;

internal static class ShipmentSeed
{
    private static readonly DateTime SeedUtc =
        new DateTime(2026, 5, 24, 12, 0, 0, DateTimeKind.Utc);

    private static readonly Address Destination =
        new Address("1 Test St", "Reno", "89501", "US");

    public static Task ScheduledAsync(IEventStore eventStore, Guid shipmentId, CancellationToken ct = default)
    {
        var line = new ShipmentLine(Guid.NewGuid(), Guid.NewGuid(), "SKU-001", 1);
        var events = new IDomainEvent[]
        {
            new ShipmentScheduled(shipmentId, Guid.NewGuid(), Destination, new[] { line }, SeedUtc),
        };
        return EventStoreSeed.AppendAsync(eventStore, StreamId.ForAggregate<Shipment>(shipmentId), events, ct);
    }

    public static Task DispatchedAsync(IEventStore eventStore, Guid shipmentId, CancellationToken ct = default)
    {
        var line = new ShipmentLine(Guid.NewGuid(), Guid.NewGuid(), "SKU-001", 1);
        var events = new IDomainEvent[]
        {
            new ShipmentScheduled(shipmentId, Guid.NewGuid(), Destination, new[] { line }, SeedUtc),
            new ShipmentDispatched(shipmentId, "TestCarrier-REF", SeedUtc),
        };
        return EventStoreSeed.AppendAsync(eventStore, StreamId.ForAggregate<Shipment>(shipmentId), events, ct);
    }

    public static Task DeliveredAsync(IEventStore eventStore, Guid shipmentId, CancellationToken ct = default)
    {
        var line = new ShipmentLine(Guid.NewGuid(), Guid.NewGuid(), "SKU-001", 1);
        var events = new IDomainEvent[]
        {
            new ShipmentScheduled(shipmentId, Guid.NewGuid(), Destination, new[] { line }, SeedUtc),
            new ShipmentDispatched(shipmentId, "TestCarrier-REF", SeedUtc),
            new ShipmentDelivered(shipmentId, SeedUtc),
        };
        return EventStoreSeed.AppendAsync(eventStore, StreamId.ForAggregate<Shipment>(shipmentId), events, ct);
    }
}
