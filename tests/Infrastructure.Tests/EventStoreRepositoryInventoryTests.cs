using EventSourcingCqrs.Application;
using EventSourcingCqrs.Domain.Fulfillment;
using EventSourcingCqrs.Infrastructure.EventStore.InMemory;
using EventSourcingCqrs.Infrastructure.Tests.TestKit;
using FluentAssertions;
using Xunit;

namespace EventSourcingCqrs.Infrastructure.Tests;

// Round-trips the Inventory aggregate through the generic EventStoreRepository
// against the in-memory event store. Parallel to EventStoreRepositoryTests
// (which round-trips Order). Validates that the generic repository handles a
// second aggregate type without per-aggregate code, and that Inventory's
// event payloads serialize and rehydrate through the same path Order uses.
public class EventStoreRepositoryInventoryTests
{
    private static readonly Guid InventoryId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid OrderId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
    private static readonly Guid LineId = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc");
    private const string Sku = "SKU-1";
    private static readonly DateTime At = new(2026, 5, 11, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task SaveAsync_then_LoadAsync_round_trip_returns_equivalent_aggregate()
    {
        var store = new InMemoryEventStore();
        var repo = new EventStoreRepository<Inventory>(store, new StubAccessor(), new StubTenantAccessor());

        var inventory = Inventory.Create(InventoryId, Sku, At);
        inventory.Adjust(100, "Initial stock", At);
        inventory.Reserve(OrderId, LineId, 10, At);

        await repo.SaveAsync(inventory, CancellationToken.None);
        var loaded = await repo.LoadAsync(InventoryId, CancellationToken.None);

        loaded.Should().NotBeNull();
        loaded!.Id.Should().Be(InventoryId);
        loaded.Sku.Should().Be(Sku);
        loaded.TotalAdjusted.Should().Be(100);
        loaded.Reserved.Should().Be(10);
        loaded.Available.Should().Be(90);
        loaded.Reservations.Should().HaveCount(1);
        loaded.Reservations[0].OrderId.Should().Be(OrderId);
        loaded.Reservations[0].LineId.Should().Be(LineId);
        loaded.Reservations[0].Sku.Should().Be(Sku);
        loaded.Reservations[0].Quantity.Should().Be(10);
        loaded.Version.Should().Be(3);
    }
}
