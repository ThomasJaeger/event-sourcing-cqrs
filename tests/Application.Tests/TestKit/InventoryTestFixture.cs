using EventSourcingCqrs.Application;
using EventSourcingCqrs.Domain.Fulfillment;
using EventSourcingCqrs.Infrastructure.EventStore.InMemory;

namespace EventSourcingCqrs.Application.Tests.TestKit;

// Shared fixture for the Inventory command-handler tests. Constructs the same
// InMemoryEventStore + EventStoreRepository + accessor stack each test wants,
// plus helpers that seed an Inventory aggregate in a target lifecycle state
// through the repository (so the seeded events land in the same store the
// handler reads). Mirrors OrderTestFixture's shape.
internal sealed class InventoryTestFixture
{
    public static readonly Guid InventoryId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    public static readonly Guid OrderId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
    public static readonly Guid LineId1 = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc");
    public const string Sku = "SKU-1";
    public static readonly DateTime At = new(2026, 5, 18, 12, 0, 0, DateTimeKind.Utc);

    public InMemoryEventStore Store { get; }
    public EventStoreRepository<Inventory> Repository { get; }
    public StubCommandContextAccessor Accessor { get; }

    public InventoryTestFixture()
    {
        Store = new InMemoryEventStore();
        Accessor = new StubCommandContextAccessor();
        Repository = new EventStoreRepository<Inventory>(Store, Accessor, new StubTenantAccessor());
    }

    public async Task SeedCreatedAsync()
    {
        var inventory = Inventory.Create(InventoryId, Sku, At);
        await Repository.SaveAsync(inventory, CancellationToken.None);
    }

    public async Task SeedWithStockAsync()
    {
        var inventory = Inventory.Create(InventoryId, Sku, At);
        inventory.Adjust(100, "Initial stock", At);
        await Repository.SaveAsync(inventory, CancellationToken.None);
    }

    public async Task SeedWithReservationAsync()
    {
        var inventory = Inventory.Create(InventoryId, Sku, At);
        inventory.Adjust(100, "Initial stock", At);
        inventory.Reserve(OrderId, LineId1, 10, At);
        await Repository.SaveAsync(inventory, CancellationToken.None);
    }

    public Task<Inventory?> LoadAsync() => Repository.LoadAsync(InventoryId, CancellationToken.None);

    public static Guid UnknownId { get; } = Guid.Parse("99999999-9999-9999-9999-999999999999");
}
