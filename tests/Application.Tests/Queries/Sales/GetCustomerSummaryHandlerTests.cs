using EventSourcingCqrs.Application.Queries.Sales;
using EventSourcingCqrs.Domain.Sales.ReadModels;
using EventSourcingCqrs.Domain.SharedKernel;
using FluentAssertions;
using Xunit;

namespace EventSourcingCqrs.Application.Tests.Queries.Sales;

public sealed class GetCustomerSummaryHandlerTests
{
    private static readonly DateTime PlacedAt = new(2026, 5, 18, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task HandleAsync_returns_the_summary_for_a_known_customer()
    {
        var customerId = Guid.NewGuid();
        var row = new CustomerSummaryRow(
            customerId, OrderCount: 3, new Money(120m, Currency.USD), PlacedAt, PlacedAt);
        var store = new StubStore(row);
        var handler = new GetCustomerSummaryHandler(store);

        var result = await handler.HandleAsync(new GetCustomerSummary(customerId), CancellationToken.None);

        result.Should().Be(row);
        // The handler passes the queried customer id through to the store.
        store.LastCustomerId.Should().Be(customerId);
    }

    [Fact]
    public async Task HandleAsync_returns_null_for_an_unknown_customer()
    {
        var store = new StubStore(null);
        var handler = new GetCustomerSummaryHandler(store);

        var result = await handler.HandleAsync(new GetCustomerSummary(Guid.NewGuid()), CancellationToken.None);

        result.Should().BeNull();
    }

    private sealed class StubStore : ICustomerSummaryStore
    {
        private readonly CustomerSummaryRow? _row;

        public StubStore(CustomerSummaryRow? row) => _row = row;

        public Guid LastCustomerId { get; private set; }

        public Task<CustomerSummaryRow?> GetAsync(Guid customerId, CancellationToken ct)
        {
            LastCustomerId = customerId;
            return Task.FromResult(_row);
        }

        // The handler reaches only GetAsync; the write path and the rebuild
        // truncate are not its contract.
        public Task<ICustomerSummaryUnitOfWork> BeginAsync(CancellationToken ct)
            => throw new NotSupportedException();

        public Task TruncateAsync(CancellationToken ct)
            => throw new NotSupportedException();
    }
}
