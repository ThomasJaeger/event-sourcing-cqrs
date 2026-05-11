using EventSourcingCqrs.Domain.Sales;
using EventSourcingCqrs.Domain.SharedKernel;
using EventSourcingCqrs.Infrastructure.EventStore.InMemory;
using FluentAssertions;
using Xunit;

namespace EventSourcingCqrs.Infrastructure.Tests;

public class EventStoreRepositoryTests
{
    private static readonly Guid OrderId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid CustomerId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid LineId = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly DateTime At = new(2026, 5, 11, 12, 0, 0, DateTimeKind.Utc);
    private static readonly Money TenUsd = new(10m, "USD");
    private static readonly Address Shipping = new("1 Main St", "Smalltown", "12345", "US");

    [Fact]
    public async Task SaveAsync_then_LoadAsync_round_trip_returns_equivalent_aggregate()
    {
        var store = new InMemoryEventStore();
        var repo = new EventStoreRepository<Order>(store);

        var order = Order.Draft(OrderId, CustomerId, At);
        order.AddLine(LineId, "SKU-1", 2, TenUsd, At);
        order.SetShippingAddress(Shipping, At);

        await repo.SaveAsync(order, CancellationToken.None);
        var loaded = await repo.LoadAsync(OrderId, CancellationToken.None);

        loaded.Should().NotBeNull();
        loaded!.Id.Should().Be(OrderId);
        loaded.Status.Should().Be(OrderStatus.Draft);
        loaded.Lines.Should().HaveCount(1);
        loaded.Lines[0].LineId.Should().Be(LineId);
        loaded.Lines[0].Sku.Should().Be("SKU-1");
        loaded.Lines[0].Quantity.Should().Be(2);
        loaded.Lines[0].UnitPrice.Should().Be(TenUsd);
        loaded.Version.Should().Be(3);
    }

    [Fact]
    public async Task SaveAsync_with_no_uncommitted_events_is_a_noop()
    {
        var store = new InMemoryEventStore();
        var repo = new EventStoreRepository<Order>(store);

        var order = Order.Draft(OrderId, CustomerId, At);
        await repo.SaveAsync(order, CancellationToken.None);

        var afterFirstSave = await store.ReadStreamAsync(OrderId, fromVersion: 0, CancellationToken.None);
        afterFirstSave.Should().HaveCount(1);

        await repo.SaveAsync(order, CancellationToken.None);

        var afterSecondSave = await store.ReadStreamAsync(OrderId, fromVersion: 0, CancellationToken.None);
        afterSecondSave.Should().HaveCount(1);
    }
}
