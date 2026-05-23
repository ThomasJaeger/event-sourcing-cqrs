using EventSourcingCqrs.Application.Queries.Fulfillment;
using EventSourcingCqrs.Domain.Fulfillment.ReadModels;
using FluentAssertions;
using Xunit;

namespace EventSourcingCqrs.Application.Tests.Queries.Fulfillment;

// The two inventory dashboard query handlers share this file because they share
// a port (IInventoryDashboardStore) and a stub implementation; splitting per
// handler would duplicate the four-method stub. The file is port-bounded, not
// handler-bounded.
public sealed class InventoryDashboardQueryHandlerTests
{
    private static readonly DateTime At = new(2026, 5, 18, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task GetBySku_returns_the_row_for_a_known_sku()
    {
        var row = new InventoryDashboardRow(Guid.NewGuid(), "SKU-1", 70, 5, At);
        var store = new StubStore(bySku: row);
        var handler = new GetInventoryDashboardBySkuHandler(store);

        var result = await handler.HandleAsync(
            new GetInventoryDashboardBySku("SKU-1"), CancellationToken.None);

        result.Should().Be(row);
        // The handler passes the queried SKU through to the store.
        store.LastSku.Should().Be("SKU-1");
    }

    [Fact]
    public async Task GetBySku_returns_null_for_an_unknown_sku()
    {
        var store = new StubStore(bySku: null);
        var handler = new GetInventoryDashboardBySkuHandler(store);

        var result = await handler.HandleAsync(
            new GetInventoryDashboardBySku("SKU-X"), CancellationToken.None);

        result.Should().BeNull();
    }

    [Fact]
    public async Task GetAll_returns_the_rows_the_store_produces()
    {
        var rows = new[]
        {
            new InventoryDashboardRow(Guid.NewGuid(), "SKU-1", 70, 5, At),
            new InventoryDashboardRow(Guid.NewGuid(), "SKU-2", 30, 0, At),
        };
        var store = new StubStore(all: rows);
        var handler = new GetAllInventoryDashboardHandler(store);

        var result = await handler.HandleAsync(new GetAllInventoryDashboard(), CancellationToken.None);

        result.Should().BeEquivalentTo(rows);
    }

    private sealed class StubStore : IInventoryDashboardStore
    {
        private readonly InventoryDashboardRow? _bySku;
        private readonly IReadOnlyList<InventoryDashboardRow> _all;

        public StubStore(
            InventoryDashboardRow? bySku = null, IReadOnlyList<InventoryDashboardRow>? all = null)
        {
            _bySku = bySku;
            _all = all ?? [];
        }

        public string? LastSku { get; private set; }

        public Task<InventoryDashboardRow?> GetBySkuAsync(string sku, CancellationToken ct)
        {
            LastSku = sku;
            return Task.FromResult(_bySku);
        }

        public Task<IReadOnlyList<InventoryDashboardRow>> GetAllAsync(CancellationToken ct)
            => Task.FromResult(_all);

        // The handlers reach only the read methods; the write path is not their
        // contract.
        public Task<IInventoryDashboardUnitOfWork> BeginAsync(CancellationToken ct)
            => throw new NotSupportedException();

        public Task TruncateAsync(CancellationToken ct)
            => throw new NotSupportedException();
    }
}
