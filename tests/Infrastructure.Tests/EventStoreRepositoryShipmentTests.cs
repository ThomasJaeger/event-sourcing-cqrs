using EventSourcingCqrs.Application;
using EventSourcingCqrs.Domain.Fulfillment;
using EventSourcingCqrs.Domain.SharedKernel;
using EventSourcingCqrs.Infrastructure.EventStore.InMemory;
using EventSourcingCqrs.Infrastructure.Tests.TestKit;
using FluentAssertions;
using Xunit;

namespace EventSourcingCqrs.Infrastructure.Tests;

// Round-trips the Shipment aggregate through the generic EventStoreRepository
// against the in-memory event store. Parallel to EventStoreRepositoryInventoryTests
// (commit 7) and EventStoreRepositoryTests (Order). Validates that the generic
// repository handles a third aggregate type without per-aggregate code, and
// that Shipment's event payloads, including the bundled ShipmentLine list
// and the Address value object on ShipmentScheduled, serialize and rehydrate
// through the same path Order and Inventory use.
public class EventStoreRepositoryShipmentTests
{
    private static readonly Guid ShipmentId = Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddddddd");
    private static readonly Guid OrderId = Guid.Parse("eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee");
    private static readonly Guid LineId = Guid.Parse("ffffffff-ffff-ffff-ffff-ffffffffffff");
    private const string Sku = "SKU-1";
    private const string CarrierReference = "CARRIER-XYZ-123";
    private const string ReturnReason = "Customer reported damage";
    private static readonly Address Destination = new(
        Street: "1 Main Street",
        City: "Berlin",
        PostalCode: "10115",
        Country: "DE");
    private static readonly DateTime At = new(2026, 5, 11, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task SaveAsync_then_LoadAsync_round_trip_returns_equivalent_aggregate()
    {
        var store = new InMemoryEventStore();
        var repo = new EventStoreRepository<Shipment>(store, new StubAccessor());

        var lines = new[] { new ShipmentLine(OrderId, LineId, Sku, 2) };
        var shipment = Shipment.Schedule(ShipmentId, OrderId, Destination, lines, At);
        shipment.Dispatch(CarrierReference, At);
        shipment.Deliver(At);
        shipment.Return(ReturnReason, At);

        await repo.SaveAsync(shipment, CancellationToken.None);
        var loaded = await repo.LoadAsync(ShipmentId, CancellationToken.None);

        loaded.Should().NotBeNull();
        loaded!.Id.Should().Be(ShipmentId);
        loaded.OrderId.Should().Be(OrderId);
        loaded.Status.Should().Be(ShipmentStatus.Returned);
        loaded.Lines.Should().HaveCount(1);
        loaded.Lines[0].OrderId.Should().Be(OrderId);
        loaded.Lines[0].LineId.Should().Be(LineId);
        loaded.Lines[0].Sku.Should().Be(Sku);
        loaded.Lines[0].Quantity.Should().Be(2);
        loaded.Version.Should().Be(4);
    }
}
