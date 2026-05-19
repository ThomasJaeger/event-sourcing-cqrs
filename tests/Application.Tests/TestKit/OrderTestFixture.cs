using EventSourcingCqrs.Application.Context;
using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.Domain.Sales;
using EventSourcingCqrs.Domain.SharedKernel;
using EventSourcingCqrs.Infrastructure.EventStore.InMemory;

namespace EventSourcingCqrs.Application.Tests.TestKit;

// Shared fixture for the Order command-handler tests. Constructs the same
// InMemoryEventStore + EventStoreRepository + accessor stack each test wants,
// plus helpers that seed an Order in a target lifecycle state through the
// repository (so the seeded events land in the same store the handler reads).
internal sealed class OrderTestFixture
{
    public static readonly Guid OrderId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    public static readonly Guid CustomerId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    public static readonly Guid LineId1 = Guid.Parse("33333333-3333-3333-3333-333333333333");
    public static readonly Money TenUsd = new(10m, "USD");
    public static readonly Address Shipping = new("1 Main St", "Smalltown", "12345", "US");
    public static readonly DateTime At = new(2026, 5, 18, 12, 0, 0, DateTimeKind.Utc);

    public InMemoryEventStore Store { get; }
    public EventStoreRepository<Order> Repository { get; }
    public StubCommandContextAccessor Accessor { get; }

    public OrderTestFixture()
    {
        Store = new InMemoryEventStore();
        Accessor = new StubCommandContextAccessor();
        Repository = new EventStoreRepository<Order>(Store, Accessor);
    }

    public async Task SeedDraftedAsync()
    {
        var order = Order.Draft(OrderId, CustomerId, At);
        await Repository.SaveAsync(order, CancellationToken.None);
    }

    public async Task SeedWithLineAsync()
    {
        var order = Order.Draft(OrderId, CustomerId, At);
        order.AddLine(LineId1, "SKU-1", 2, TenUsd, At);
        await Repository.SaveAsync(order, CancellationToken.None);
    }

    public async Task SeedReadyToPlaceAsync()
    {
        var order = Order.Draft(OrderId, CustomerId, At);
        order.AddLine(LineId1, "SKU-1", 2, TenUsd, At);
        order.SetShippingAddress(Shipping, At);
        await Repository.SaveAsync(order, CancellationToken.None);
    }

    public async Task SeedPlacedAsync()
    {
        var order = Order.Draft(OrderId, CustomerId, At);
        order.AddLine(LineId1, "SKU-1", 2, TenUsd, At);
        order.SetShippingAddress(Shipping, At);
        order.Place(At);
        await Repository.SaveAsync(order, CancellationToken.None);
    }

    public Task<Order?> LoadAsync() => Repository.LoadAsync(OrderId, CancellationToken.None);

    public static Guid UnknownId { get; } = Guid.Parse("99999999-9999-9999-9999-999999999999");
}
