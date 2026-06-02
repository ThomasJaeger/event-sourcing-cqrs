using System.Net;
using System.Net.Http.Json;
using EventSourcingCqrs.Application.Queries.Sales;
using EventSourcingCqrs.Domain.Sales;
using EventSourcingCqrs.Domain.Sales.ReadModels;
using EventSourcingCqrs.Domain.SharedKernel;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using NpgsqlTypes;
using Xunit;

namespace EventSourcingCqrs.IntegrationTests.Queries.Sales;

public class GetOrderDetailTests : IClassFixture<ApiFixture>
{
    private readonly ApiFixture _fixture;

    private static readonly DateTime SeededAt =
        new DateTime(2026, 5, 24, 12, 0, 0, DateTimeKind.Utc);

    private static readonly Guid OtherTenant = Guid.Parse("55555555-5555-5555-5555-555555555555");

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

    [Fact]
    public async Task GetOrderDetail_for_a_header_seeded_under_another_tenant_returns_404()
    {
        var client = _fixture.Factory.CreateClient();
        var orderId = Guid.NewGuid();
        // The header exists under another tenant; the default-tenant query the bus runs cannot see
        // it, so the composed view is null and the endpoint maps that to not found.
        await SeedOrderHeaderUnderTenantAsync(orderId, Guid.NewGuid(), OtherTenant);

        var response = await client.PostQueryAsync("GetOrderDetail", new { orderId });

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // Direct-inserts an order-detail header under an explicit tenant. The unit-of-work seed path
    // writes the default tenant only, so a cross-tenant row is seeded by raw insert (ApiFixture
    // exposes the connection string).
    private async Task SeedOrderHeaderUnderTenantAsync(Guid orderId, Guid customerId, Guid tenant)
    {
        await using var connection = new NpgsqlConnection(_fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            "INSERT INTO read_models.order_detail (order_id, customer_id, status, last_updated_utc, tenant_id) " +
            "VALUES (@order_id, @customer_id, @status, @last_updated_utc, @tenant_id)";
        command.Parameters.AddWithValue("order_id", NpgsqlDbType.Uuid, orderId);
        command.Parameters.AddWithValue("customer_id", NpgsqlDbType.Uuid, customerId);
        command.Parameters.AddWithValue("status", NpgsqlDbType.Text, "Placed");
        command.Parameters.AddWithValue("last_updated_utc", NpgsqlDbType.TimestampTz, SeededAt);
        command.Parameters.AddWithValue("tenant_id", NpgsqlDbType.Uuid, tenant);
        await command.ExecuteNonQueryAsync();
    }
}
