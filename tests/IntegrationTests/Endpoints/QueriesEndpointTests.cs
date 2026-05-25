using System.Net;
using System.Net.Http.Json;
using EventSourcingCqrs.Application.Queries.Sales;
using EventSourcingCqrs.Domain.Fulfillment.ReadModels;
using EventSourcingCqrs.Domain.Sales;
using EventSourcingCqrs.Domain.Sales.ReadModels;
using EventSourcingCqrs.Domain.SharedKernel;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EventSourcingCqrs.IntegrationTests.Endpoints;

// The Api host does not run the outbox processor (Commit 8's split, against the
// data-loss bug), so dispatching a command through /commands appends events but
// never drives a projection: the read models stay empty. So these tests seed
// read-model state directly through the stores' unit-of-work surface, resolved
// from the WebApplicationFactory's DI, which is the same write path projections
// use. The checkpoint each CommitAsync advances is inert here, since no projection
// in this host reads it; the seed names below keep those checkpoint rows distinct
// so one test's seed never collides with another's on the class-shared database.
public class QueriesEndpointTests : IClassFixture<ApiFixture>
{
    private static readonly DateTime SeededAt = new(2026, 5, 14, 9, 0, 0, DateTimeKind.Utc);

    private readonly ApiFixture _fixture;

    public QueriesEndpointTests(ApiFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Posting_an_unknown_query_type_returns_400()
    {
        var client = _fixture.Factory.CreateClient();
        var response = await client.PostQueryAsync("NotAQuery", new { anything = 1 });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Posting_a_payload_that_fails_to_deserialize_returns_400()
    {
        var client = _fixture.Factory.CreateClient();
        var response = await client.PostQueryAsync("GetOrderDetail", new { orderId = "not-a-guid" });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task GetAllInventoryDashboard_on_an_empty_read_model_returns_200_with_an_empty_array()
    {
        var client = _fixture.Factory.CreateClient();

        // No seed: the empty read model is the Api host's natural state, and a list
        // query returns 200 with [] rather than 404.
        var response = await client.PostQueryAsync("GetAllInventoryDashboard", new { });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var rows = await response.Content.ReadFromJsonAsync<List<InventoryDashboardRow>>();
        rows.Should().NotBeNull();
        rows.Should().BeEmpty();
    }

    [Fact]
    public async Task ListOrders_returns_seeded_rows_newest_first()
    {
        var client = _fixture.Factory.CreateClient();
        var older = SampleOrderRow(Guid.NewGuid()) with { PlacedUtc = SeededAt.AddDays(-1) };
        var newer = SampleOrderRow(Guid.NewGuid()) with { PlacedUtc = SeededAt };
        var store = _fixture.Factory.Services.GetRequiredService<IOrderListStore>();
        await using (var uow = await store.BeginAsync(CancellationToken.None))
        {
            await uow.InsertAsync(older, CancellationToken.None);
            await uow.InsertAsync(newer, CancellationToken.None);
            await uow.CommitAsync("seed-order-list", 1, CancellationToken.None);
        }

        var response = await client.PostQueryAsync("ListOrders", new { offset = 0, limit = 50 });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var rows = await response.Content.ReadFromJsonAsync<List<OrderListRow>>();
        rows.Should().NotBeNull();
        rows!.Select(r => r.OrderId).Should().ContainInOrder(newer.OrderId, older.OrderId);
    }

    [Fact]
    public async Task GetOrderDetail_for_an_unknown_order_returns_404()
    {
        var client = _fixture.Factory.CreateClient();
        var response = await client.PostQueryAsync("GetOrderDetail", new { orderId = Guid.NewGuid() });

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task GetOrderDetail_for_a_seeded_order_returns_200_with_the_header()
    {
        var client = _fixture.Factory.CreateClient();
        var orderId = Guid.NewGuid();
        var customerId = Guid.NewGuid();
        var store = _fixture.Factory.Services.GetRequiredService<IOrderDetailStore>();
        await using (var uow = await store.BeginAsync(CancellationToken.None))
        {
            // The header alone makes GetOrderDetail return a view: the handler reads
            // lines and timeline only after a non-null header, so an order with no
            // lines yet still resolves to a 200 with empty lines and timeline.
            await uow.CreateHeaderAsync(orderId, customerId, SeededAt, CancellationToken.None);
            await uow.CommitAsync("seed-order-detail", 1, CancellationToken.None);
        }

        var response = await client.PostQueryAsync("GetOrderDetail", new { orderId });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var view = await response.Content.ReadFromJsonAsync<OrderDetailView>();
        view.Should().NotBeNull();
        view!.Header.OrderId.Should().Be(orderId);
        view.Lines.Should().BeEmpty();
        view.Timeline.Should().BeEmpty();
    }

    [Fact]
    public async Task GetCustomerSummary_for_a_seeded_customer_returns_200_with_the_summary()
    {
        var client = _fixture.Factory.CreateClient();
        var customerId = Guid.NewGuid();
        var total = new Money(149.95m, Currency.USD);
        var store = _fixture.Factory.Services.GetRequiredService<ICustomerSummaryStore>();
        await using (var uow = await store.BeginAsync(CancellationToken.None))
        {
            // One placement creates the summary: order_count 1, lifetime value the
            // order total. The summary surface accumulates rather than setting, so
            // a single placement is the smallest non-null state.
            await uow.ApplyPlacementAsync(customerId, total, SeededAt, SeededAt, CancellationToken.None);
            await uow.CommitAsync("seed-customer-summary", 1, CancellationToken.None);
        }

        var response = await client.PostQueryAsync("GetCustomerSummary", new { customerId });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var summary = await response.Content.ReadFromJsonAsync<CustomerSummaryRow>();
        summary.Should().NotBeNull();
        summary!.CustomerId.Should().Be(customerId);
        summary.OrderCount.Should().Be(1);
        summary.LifetimeValue.Amount.Should().Be(149.95m);
    }

    [Fact]
    public async Task GetCustomerSummary_for_an_unknown_customer_returns_404()
    {
        var client = _fixture.Factory.CreateClient();
        var response = await client.PostQueryAsync("GetCustomerSummary", new { customerId = Guid.NewGuid() });

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task GetInventoryDashboardBySku_for_a_seeded_sku_returns_200_with_the_row()
    {
        var client = _fixture.Factory.CreateClient();
        var inventoryId = Guid.NewGuid();
        var sku = $"SKU-{Guid.NewGuid():N}";
        var store = _fixture.Factory.Services.GetRequiredService<IInventoryDashboardStore>();
        await using (var uow = await store.BeginAsync(CancellationToken.None))
        {
            // Create the row, then move on-hand off zero so the assertion proves the
            // adjust path, not just the create.
            await uow.CreateDashboardAsync(inventoryId, sku, SeededAt, CancellationToken.None);
            await uow.AdjustOnHandAsync(inventoryId, 7, SeededAt, CancellationToken.None);
            await uow.CommitAsync("seed-inventory-dashboard", 1, CancellationToken.None);
        }

        var response = await client.PostQueryAsync("GetInventoryDashboardBySku", new { sku });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var row = await response.Content.ReadFromJsonAsync<InventoryDashboardRow>();
        row.Should().NotBeNull();
        row!.Sku.Should().Be(sku);
        row.OnHandQuantity.Should().Be(7);
    }

    private static OrderListRow SampleOrderRow(Guid orderId)
        => new(
            OrderId: orderId,
            CustomerId: Guid.NewGuid(),
            Status: OrderStatus.Placed,
            Total: new Money(149.95m, Currency.USD),
            PlacedUtc: SeededAt,
            LastUpdatedUtc: SeededAt,
            IsReturned: false,
            ReturnedUtc: null);
}
