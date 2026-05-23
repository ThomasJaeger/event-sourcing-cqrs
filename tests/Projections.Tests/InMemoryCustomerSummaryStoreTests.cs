using EventSourcingCqrs.Domain.Sales.ReadModels;
using EventSourcingCqrs.Domain.SharedKernel;
using FluentAssertions;
using Xunit;

namespace EventSourcingCqrs.Projections.Tests;

// Behaviour of the in-memory CustomerSummary double, the test seam the projection
// (commit 8) and the rebuild test run against. The projection's own logic (the
// not-found-on-cancel no-op, the place-then-cancel netting) lands in the
// projection tests; these cases pin the store mechanics the projection relies on.
public class InMemoryCustomerSummaryStoreTests
{
    private const string ProjectionName = "customer-summary";
    private static readonly DateTime PlacedAt = new(2026, 5, 14, 9, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime LaterAt = new(2026, 5, 20, 9, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime SystemAt = new(2026, 5, 14, 9, 0, 5, DateTimeKind.Utc);

    [Fact]
    public async Task ApplyPlacement_creates_a_row_with_count_one_and_lifetime_equal_to_total()
    {
        var store = new InMemoryCustomerSummaryStore();
        var customerId = Guid.NewGuid();
        await using (var uow = await store.BeginAsync(CancellationToken.None))
        {
            await uow.ApplyPlacementAsync(
                customerId, new Money(50m, Currency.USD), PlacedAt, SystemAt, CancellationToken.None);
            await uow.CommitAsync(ProjectionName, 1, CancellationToken.None);
        }

        var row = await store.GetAsync(customerId, CancellationToken.None);
        row!.OrderCount.Should().Be(1);
        row.LifetimeValue.Should().Be(new Money(50m, Currency.USD));
        row.LastOrderUtc.Should().Be(PlacedAt);
    }

    [Fact]
    public async Task ApplyPlacement_twice_accumulates_and_advances_last_order_by_max()
    {
        var store = new InMemoryCustomerSummaryStore();
        var customerId = Guid.NewGuid();
        await using (var uow = await store.BeginAsync(CancellationToken.None))
        {
            // Later placement first, then an earlier one: last_order takes the MAX.
            await uow.ApplyPlacementAsync(
                customerId, new Money(50m, Currency.USD), LaterAt, SystemAt, CancellationToken.None);
            await uow.ApplyPlacementAsync(
                customerId, new Money(30m, Currency.USD), PlacedAt, SystemAt, CancellationToken.None);
            await uow.CommitAsync(ProjectionName, 2, CancellationToken.None);
        }

        var row = await store.GetAsync(customerId, CancellationToken.None);
        row!.OrderCount.Should().Be(2);
        row.LifetimeValue.Should().Be(new Money(80m, Currency.USD));
        row.LastOrderUtc.Should().Be(LaterAt);
    }

    [Fact]
    public async Task ApplyCancellation_decrements_and_leaves_last_order_unchanged()
    {
        var store = new InMemoryCustomerSummaryStore();
        var customerId = Guid.NewGuid();
        await using (var uow = await store.BeginAsync(CancellationToken.None))
        {
            await uow.ApplyPlacementAsync(
                customerId, new Money(50m, Currency.USD), PlacedAt, SystemAt, CancellationToken.None);
            await uow.ApplyPlacementAsync(
                customerId, new Money(30m, Currency.USD), LaterAt, SystemAt, CancellationToken.None);
            await uow.ApplyCancellationAsync(
                customerId, new Money(30m, Currency.USD), SystemAt, CancellationToken.None);
            await uow.CommitAsync(ProjectionName, 3, CancellationToken.None);
        }

        var row = await store.GetAsync(customerId, CancellationToken.None);
        row!.OrderCount.Should().Be(1);
        row.LifetimeValue.Should().Be(new Money(50m, Currency.USD));
        // last_order_utc is not recomputed on cancellation (ADR 0019); it stays
        // at the later placement.
        row.LastOrderUtc.Should().Be(LaterAt);
    }

    [Fact]
    public async Task Per_order_lookup_round_trips_by_order_id_and_deletes()
    {
        var store = new InMemoryCustomerSummaryStore();
        var customerId = Guid.NewGuid();
        var orderId = Guid.NewGuid();
        var orderRow = new CustomerSummaryOrderRow(
            customerId, orderId, new Money(40m, Currency.USD), PlacedAt);
        await using (var uow = await store.BeginAsync(CancellationToken.None))
        {
            await uow.InsertOrderAsync(orderRow, CancellationToken.None);
            await uow.CommitAsync(ProjectionName, 1, CancellationToken.None);
        }

        await using (var uow = await store.BeginAsync(CancellationToken.None))
        {
            // Cancellation looks up by order id alone (no customer id on the event).
            (await uow.GetOrderByOrderIdAsync(orderId, CancellationToken.None)).Should().Be(orderRow);
            await uow.DeleteOrderAsync(customerId, orderId, CancellationToken.None);
            await uow.CommitAsync(ProjectionName, 2, CancellationToken.None);
        }

        await using var read = await store.BeginAsync(CancellationToken.None);
        (await read.GetOrderByOrderIdAsync(orderId, CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task GetOrderByOrderId_returns_null_when_absent()
    {
        var store = new InMemoryCustomerSummaryStore();
        await using var uow = await store.BeginAsync(CancellationToken.None);
        (await uow.GetOrderByOrderIdAsync(Guid.NewGuid(), CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task Truncate_clears_summaries_and_the_per_order_lookup()
    {
        var store = new InMemoryCustomerSummaryStore();
        var customerId = Guid.NewGuid();
        var orderId = Guid.NewGuid();
        await using (var uow = await store.BeginAsync(CancellationToken.None))
        {
            await uow.ApplyPlacementAsync(
                customerId, new Money(50m, Currency.USD), PlacedAt, SystemAt, CancellationToken.None);
            await uow.InsertOrderAsync(
                new CustomerSummaryOrderRow(customerId, orderId, new Money(50m, Currency.USD), PlacedAt),
                CancellationToken.None);
            await uow.CommitAsync(ProjectionName, 1, CancellationToken.None);
        }

        await store.TruncateAsync(CancellationToken.None);

        (await store.GetAsync(customerId, CancellationToken.None)).Should().BeNull();
        await using var read = await store.BeginAsync(CancellationToken.None);
        (await read.GetOrderByOrderIdAsync(orderId, CancellationToken.None)).Should().BeNull();
    }
}
