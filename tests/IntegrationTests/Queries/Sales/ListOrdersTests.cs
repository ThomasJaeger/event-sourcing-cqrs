using System.Net;
using System.Net.Http.Json;
using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.Domain.Sales;
using EventSourcingCqrs.Domain.Sales.ReadModels;
using EventSourcingCqrs.Domain.SharedKernel;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EventSourcingCqrs.IntegrationTests.Queries.Sales;

public class ListOrdersTests : IClassFixture<ApiFixture>
{
    private readonly ApiFixture _fixture;

    private static readonly DateTime SeededAt =
        new DateTime(2026, 5, 24, 12, 0, 0, DateTimeKind.Utc);

    public ListOrdersTests(ApiFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task ListOrders_returns_seeded_rows_newest_first()
    {
        var client = _fixture.Factory.CreateClient();
        var older = SampleOrderRow(SeededAt.AddMinutes(-10));
        var newer = SampleOrderRow(SeededAt);

        var store = _fixture.Factory.Services.GetRequiredService<IOrderListStore>();
        await store.TruncateAsync(default);
        await _fixture.SeedAsTenantAsync(WellKnownTenants.Default, async () =>
        {
            await using (var uow = await store.BeginAsync(default))
            {
                await uow.InsertAsync(older, default);
                await uow.InsertAsync(newer, default);
                await uow.CommitAsync("list-orders-seed-1", 1, default);
            }
        });

        var response = await client.PostQueryAsync(
            "ListOrders",
            new { offset = 0, limit = 10 });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var rows = await response.Content.ReadFromJsonAsync<IReadOnlyList<OrderListRow>>();
        rows.Should().NotBeNull();
        rows!.Should().HaveCount(2);
        rows[0].OrderId.Should().Be(newer.OrderId);
        rows[1].OrderId.Should().Be(older.OrderId);
    }

    [Fact]
    public async Task ListOrders_with_offset_and_limit_returns_the_correct_page()
    {
        var client = _fixture.Factory.CreateClient();
        var rows = Enumerable
            .Range(0, 5)
            .Select(i => SampleOrderRow(SeededAt.AddMinutes(-i)))
            .ToList();

        var store = _fixture.Factory.Services.GetRequiredService<IOrderListStore>();
        await store.TruncateAsync(default);
        await _fixture.SeedAsTenantAsync(WellKnownTenants.Default, async () =>
        {
            await using (var uow = await store.BeginAsync(default))
            {
                foreach (var row in rows)
                {
                    await uow.InsertAsync(row, default);
                }
                await uow.CommitAsync("list-orders-seed-2", 1, default);
            }
        });

        var response = await client.PostQueryAsync(
            "ListOrders",
            new { offset = 2, limit = 2 });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var page = await response.Content.ReadFromJsonAsync<IReadOnlyList<OrderListRow>>();
        page.Should().NotBeNull();
        page!.Should().HaveCount(2);
        page[0].OrderId.Should().Be(rows[2].OrderId);
        page[1].OrderId.Should().Be(rows[3].OrderId);
    }

    private static OrderListRow SampleOrderRow(DateTime placedUtc) => new OrderListRow(
        OrderId: Guid.NewGuid(),
        CustomerId: Guid.NewGuid(),
        Status: OrderStatus.Placed,
        Total: new Money(149.95m, Currency.USD),
        PlacedUtc: placedUtc,
        LastUpdatedUtc: placedUtc,
        IsReturned: false,
        ReturnedUtc: null);
}
