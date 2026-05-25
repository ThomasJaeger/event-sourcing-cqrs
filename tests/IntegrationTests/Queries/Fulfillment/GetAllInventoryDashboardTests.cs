using System.Net;
using System.Net.Http.Json;
using EventSourcingCqrs.Domain.Fulfillment.ReadModels;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EventSourcingCqrs.IntegrationTests.Queries.Fulfillment;

public class GetAllInventoryDashboardTests : IClassFixture<ApiFixture>
{
    private readonly ApiFixture _fixture;

    private static readonly DateTime SeededAt =
        new DateTime(2026, 5, 24, 12, 0, 0, DateTimeKind.Utc);

    public GetAllInventoryDashboardTests(ApiFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task GetAllInventoryDashboard_on_an_empty_read_model_returns_200_with_an_empty_array()
    {
        var client = _fixture.Factory.CreateClient();
        var store = _fixture.Factory.Services.GetRequiredService<IInventoryDashboardStore>();
        await store.TruncateAsync(default);

        var response = await client.PostQueryAsync(
            "GetAllInventoryDashboard",
            new { });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var rows = await response.Content.ReadFromJsonAsync<IReadOnlyList<InventoryDashboardRow>>();
        rows.Should().NotBeNull();
        rows!.Should().BeEmpty();
    }

    [Fact]
    public async Task GetAllInventoryDashboard_returns_all_seeded_rows()
    {
        var client = _fixture.Factory.CreateClient();
        var skus = new[] { "SKU-A", "SKU-B", "SKU-C" };

        var store = _fixture.Factory.Services.GetRequiredService<IInventoryDashboardStore>();
        await store.TruncateAsync(default);
        await using (var uow = await store.BeginAsync(default))
        {
            foreach (var sku in skus)
            {
                var inventoryId = Guid.NewGuid();
                await uow.CreateDashboardAsync(inventoryId, sku, SeededAt, default);
                await uow.AdjustOnHandAsync(inventoryId, 10, SeededAt, default);
            }
            await uow.CommitAsync("get-all-inventory-dashboard-seed", 1, default);
        }

        var response = await client.PostQueryAsync(
            "GetAllInventoryDashboard",
            new { });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var rows = await response.Content.ReadFromJsonAsync<IReadOnlyList<InventoryDashboardRow>>();
        rows.Should().NotBeNull();
        rows!.Should().HaveCount(3);
        rows.Select(r => r.Sku).Should().BeEquivalentTo(skus);
    }
}
