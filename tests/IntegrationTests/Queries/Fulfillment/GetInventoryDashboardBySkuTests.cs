using System.Net;
using System.Net.Http.Json;
using EventSourcingCqrs.Domain.Fulfillment.ReadModels;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EventSourcingCqrs.IntegrationTests.Queries.Fulfillment;

public class GetInventoryDashboardBySkuTests : IClassFixture<ApiFixture>
{
    private readonly ApiFixture _fixture;

    private static readonly DateTime SeededAt =
        new DateTime(2026, 5, 24, 12, 0, 0, DateTimeKind.Utc);

    public GetInventoryDashboardBySkuTests(ApiFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task GetInventoryDashboardBySku_for_a_seeded_sku_returns_200_with_the_row()
    {
        var client = _fixture.Factory.CreateClient();
        var inventoryId = Guid.NewGuid();
        const string sku = "SKU-EXISTING";

        var store = _fixture.Factory.Services.GetRequiredService<IInventoryDashboardStore>();
        await using (var uow = await store.BeginAsync(default))
        {
            await uow.CreateDashboardAsync(inventoryId, sku, SeededAt, default);
            await uow.AdjustOnHandAsync(inventoryId, 25, SeededAt, default);
            await uow.CommitAsync("get-inventory-by-sku-seed", 1, default);
        }

        var response = await client.PostQueryAsync(
            "GetInventoryDashboardBySku",
            new { sku });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var row = await response.Content.ReadFromJsonAsync<InventoryDashboardRow>();
        row.Should().NotBeNull();
        row!.Sku.Should().Be(sku);
        row.InventoryId.Should().Be(inventoryId);
    }

    [Fact]
    public async Task GetInventoryDashboardBySku_for_an_unknown_sku_returns_404()
    {
        var client = _fixture.Factory.CreateClient();

        var response = await client.PostQueryAsync(
            "GetInventoryDashboardBySku",
            new { sku = "SKU-NONEXISTENT" });

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}
