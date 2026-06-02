using System.Net;
using System.Net.Http.Json;
using EventSourcingCqrs.Application.Queries.Sales;
using EventSourcingCqrs.Domain.Sales;
using EventSourcingCqrs.Domain.Sales.ReadModels;
using EventSourcingCqrs.Domain.SharedKernel;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EventSourcingCqrs.IntegrationTests.Queries.Sales;

public class GetOrderDetailTests : IClassFixture<ApiFixture>
{
    private readonly ApiFixture _fixture;

    private static readonly DateTime SeededAt =
        new DateTime(2026, 5, 24, 12, 0, 0, DateTimeKind.Utc);

    public GetOrderDetailTests(ApiFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task GetOrderDetail_for_an_unknown_order_returns_404()
    {
        var client = _fixture.Factory.CreateClient();

        var response = await client.PostQueryAsync(
            "GetOrderDetail",
            new { orderId = Guid.NewGuid() });

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task GetOrderDetail_for_a_seeded_order_returns_200_with_the_header()
    {
        var client = _fixture.Factory.CreateClient();
        var orderId = Guid.NewGuid();
        var customerId = Guid.NewGuid();

        var store = _fixture.Factory.Services.GetRequiredService<IOrderDetailStore>();
        await using (var uow = await store.BeginAsync(default))
        {
            await uow.CreateHeaderAsync(orderId, customerId, SeededAt, default);
            await uow.CommitAsync("get-order-detail-header-seed", 1, default);
        }

        var response = await client.PostQueryAsync(
            "GetOrderDetail",
            new { orderId });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var view = await response.Content.ReadFromJsonAsync<OrderDetailView>();
        view.Should().NotBeNull();
        view!.Header.OrderId.Should().Be(orderId);
        view.Header.CustomerId.Should().Be(customerId);
        view.Lines.Should().BeEmpty();
        view.Timeline.Should().BeEmpty();
    }

    [Fact]
    public async Task GetOrderDetail_for_a_placed_order_returns_total_and_status()
    {
        var client = _fixture.Factory.CreateClient();
        var orderId = Guid.NewGuid();
        var customerId = Guid.NewGuid();
        var placedUtc = SeededAt.AddMinutes(1);

        var store = _fixture.Factory.Services.GetRequiredService<IOrderDetailStore>();
        await using (var uow = await store.BeginAsync(default))
        {
            await uow.CreateHeaderAsync(orderId, customerId, SeededAt, default);
            await uow.ApplyPlacedAsync(
                orderId,
                new Money(99.95m, Currency.USD),
                placedUtc,
                placedUtc,
                default);
            await uow.CommitAsync("get-order-detail-placed-seed", 1, default);
        }

        var response = await client.PostQueryAsync(
            "GetOrderDetail",
            new { orderId });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var view = await response.Content.ReadFromJsonAsync<OrderDetailView>();
        view.Should().NotBeNull();
        view!.Header.OrderId.Should().Be(orderId);
        view.Header.Total!.Amount.Should().Be(99.95m);
        view.Header.Status.Should().Be(OrderStatus.Placed);
    }

    // The cross-tenant case lives in CrossTenantQueryCases with the other query types, so the
    // isolation logic runs both here, named for the query, and from the registry-driven coverage
    // test. The shared raw-insert seed names the tenant explicitly, the way a second tenant's
    // projection would have written the header.
    [Fact]
    public Task GetOrderDetail_for_a_header_seeded_under_another_tenant_returns_404()
        => CrossTenantQueryCases.For(_fixture)[typeof(GetOrderDetail)]();
}
