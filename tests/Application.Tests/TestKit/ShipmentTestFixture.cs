using EventSourcingCqrs.Application;
using EventSourcingCqrs.Domain.Fulfillment;
using EventSourcingCqrs.Domain.SharedKernel;
using EventSourcingCqrs.Infrastructure.EventStore.InMemory;

namespace EventSourcingCqrs.Application.Tests.TestKit;

// Shared fixture for the Shipment command-handler tests. Constructs the same
// InMemoryEventStore + EventStoreRepository + accessor stack each test wants,
// plus helpers that seed a Shipment in a target lifecycle state through the
// repository (so the seeded events land in the same store the handler reads).
// Mirrors OrderTestFixture and InventoryTestFixture.
internal sealed class ShipmentTestFixture
{
    public static readonly Guid ShipmentId = Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddddddd");
    public static readonly Guid OrderId = Guid.Parse("eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee");
    public static readonly Guid LineId = Guid.Parse("ffffffff-ffff-ffff-ffff-ffffffffffff");
    public const string Sku = "SKU-1";
    public const string CarrierReference = "CARRIER-XYZ-123";
    public static readonly Address Destination = new(
        Street: "1 Main Street",
        City: "Berlin",
        PostalCode: "10115",
        Country: "DE");
    public static readonly DateTime At = new(2026, 5, 18, 12, 0, 0, DateTimeKind.Utc);

    public static readonly IReadOnlyList<ShipmentLine> Lines =
        [new ShipmentLine(OrderId, LineId, Sku, 2)];

    public InMemoryEventStore Store { get; }
    public EventStoreRepository<Shipment> Repository { get; }
    public StubCommandContextAccessor Accessor { get; }

    public ShipmentTestFixture()
    {
        Store = new InMemoryEventStore();
        Accessor = new StubCommandContextAccessor();
        Repository = new EventStoreRepository<Shipment>(Store, Accessor, new StubTenantAccessor(), new StubCurrentVersions());
    }

    public async Task SeedScheduledAsync()
    {
        var shipment = Shipment.Schedule(ShipmentId, OrderId, Destination, Lines, At);
        await Repository.SaveAsync(shipment, CancellationToken.None);
    }

    public async Task SeedDispatchedAsync()
    {
        var shipment = Shipment.Schedule(ShipmentId, OrderId, Destination, Lines, At);
        shipment.Dispatch(CarrierReference, At);
        await Repository.SaveAsync(shipment, CancellationToken.None);
    }

    public async Task SeedDeliveredAsync()
    {
        var shipment = Shipment.Schedule(ShipmentId, OrderId, Destination, Lines, At);
        shipment.Dispatch(CarrierReference, At);
        shipment.Deliver(At);
        await Repository.SaveAsync(shipment, CancellationToken.None);
    }

    public Task<Shipment?> LoadAsync() => Repository.LoadAsync(ShipmentId, CancellationToken.None);

    public static Guid UnknownId { get; } = Guid.Parse("99999999-9999-9999-9999-999999999999");
}
