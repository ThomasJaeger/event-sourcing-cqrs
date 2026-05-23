using EventSourcingCqrs.Application.Queries.Sales;
using EventSourcingCqrs.Domain.Sales;
using EventSourcingCqrs.Domain.Sales.ReadModels;
using EventSourcingCqrs.Domain.SharedKernel;
using FluentAssertions;
using Xunit;

namespace EventSourcingCqrs.Application.Tests.Queries.Sales;

public sealed class ListOrdersHandlerTests
{
    private static readonly DateTime PlacedAt = new(2026, 5, 18, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task HandleAsync_returns_the_rows_the_store_produces()
    {
        var rows = new[]
        {
            SampleRow(Guid.NewGuid()),
            SampleRow(Guid.NewGuid())
        };
        var store = new StubStore(rows);
        var handler = new ListOrdersHandler(store);

        var result = await handler.HandleAsync(new ListOrders(Offset: 0, Limit: 50), CancellationToken.None);

        result.Should().BeEquivalentTo(rows);
    }

    [Fact]
    public async Task HandleAsync_propagates_offset_and_limit_to_the_store()
    {
        var store = new StubStore(Array.Empty<OrderListRow>());
        var handler = new ListOrdersHandler(store);

        await handler.HandleAsync(new ListOrders(Offset: 25, Limit: 10), CancellationToken.None);

        store.LastOffset.Should().Be(25);
        store.LastLimit.Should().Be(10);
    }

    private static OrderListRow SampleRow(Guid orderId)
        => new(
            OrderId: orderId,
            CustomerId: Guid.NewGuid(),
            Status: OrderStatus.Placed,
            Total: new Money(50m, Currency.USD),
            PlacedUtc: PlacedAt,
            LastUpdatedUtc: PlacedAt,
            IsReturned: false,
            ReturnedUtc: null);

    private sealed class StubStore : IOrderListStore
    {
        private readonly IReadOnlyList<OrderListRow> _rows;

        public StubStore(IReadOnlyList<OrderListRow> rows) => _rows = rows;

        public int LastOffset { get; private set; }
        public int LastLimit { get; private set; }

        public Task<IReadOnlyList<OrderListRow>> GetPageAsync(int offset, int limit, CancellationToken ct)
        {
            LastOffset = offset;
            LastLimit = limit;
            return Task.FromResult(_rows);
        }

        public Task<IOrderListUnitOfWork> BeginAsync(CancellationToken ct)
            => throw new NotSupportedException("Handler should not need a unit of work.");

        public Task<OrderListRow?> GetAsync(Guid orderId, CancellationToken ct)
            => throw new NotSupportedException("Handler should not call GetAsync.");

        public Task TruncateAsync(CancellationToken ct)
            => throw new NotSupportedException("Handler should not truncate.");
    }
}
