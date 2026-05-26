using Bunit;
using EventSourcingCqrs.Application.Queries.Fulfillment;
using EventSourcingCqrs.Domain.Fulfillment.ReadModels;
using EventSourcingCqrs.Hosts.Web;
using EventSourcingCqrs.Hosts.Web.Components.Pages;
using EventSourcingCqrs.Hosts.Web.Tests.TestDoubles;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EventSourcingCqrs.Hosts.Web.Tests.Components;

public class InventoryDashboardPageTests : BunitContext
{
    private readonly StubApiClient stubApiClient = new();

    public InventoryDashboardPageTests()
    {
        Services.AddSingleton<IApiClient>(stubApiClient);
        // The page injects TimeProvider for its create-polling loop. These
        // render-only tests never dispatch, so the system clock satisfies it.
        Services.AddSingleton(TimeProvider.System);
    }

    [Fact]
    public void Inventory_page_renders_table_header_with_expected_columns()
    {
        stubApiClient.EnqueueQueryResult<GetAllInventoryDashboard, IReadOnlyList<InventoryDashboardRow>>(
            new[] { SampleRow("SKU-1") });

        var cut = Render<InventoryDashboard>();

        var headers = cut.FindAll("th").Select(th => th.TextContent.Trim()).ToList();
        headers.Should().Equal("SKU", "On Hand", "Reserved", "Last Updated");
    }

    [Fact]
    public void Inventory_page_renders_one_row_per_sku_returned()
    {
        stubApiClient.EnqueueQueryResult<GetAllInventoryDashboard, IReadOnlyList<InventoryDashboardRow>>(
            new[] { SampleRow("SKU-1"), SampleRow("SKU-2"), SampleRow("SKU-3") });

        var cut = Render<InventoryDashboard>();

        cut.FindAll("tbody tr").Should().HaveCount(3);
    }

    [Fact]
    public void Inventory_page_renders_empty_state_when_no_inventory()
    {
        stubApiClient.EnqueueQueryResult<GetAllInventoryDashboard, IReadOnlyList<InventoryDashboardRow>>(
            Array.Empty<InventoryDashboardRow>());

        var cut = Render<InventoryDashboard>();

        cut.Markup.Should().Contain("No inventory yet");
    }

    [Fact]
    public void Inventory_page_renders_sku_onhand_and_reserved_in_columns()
    {
        stubApiClient.EnqueueQueryResult<GetAllInventoryDashboard, IReadOnlyList<InventoryDashboardRow>>(
            new[] { SampleRow("SKU-1", onHand: 42, reserved: 7) });

        var cut = Render<InventoryDashboard>();

        var cells = cut.FindAll("tbody tr td").Select(td => td.TextContent.Trim()).ToList();
        cells[0].Should().Be("SKU-1");
        cells[1].Should().Be("42");
        cells[2].Should().Be("7");
    }

    [Fact]
    public void Inventory_page_queries_GetAllInventoryDashboard_on_initialization()
    {
        stubApiClient.EnqueueQueryResult<GetAllInventoryDashboard, IReadOnlyList<InventoryDashboardRow>>(
            Array.Empty<InventoryDashboardRow>());

        Render<InventoryDashboard>();

        stubApiClient.CapturedQueries.Should().ContainSingle()
            .Which.Should().BeOfType<GetAllInventoryDashboard>();
    }

    [Fact]
    public void Inventory_page_renders_the_create_inventory_button()
    {
        stubApiClient.EnqueueQueryResult<GetAllInventoryDashboard, IReadOnlyList<InventoryDashboardRow>>(
            Array.Empty<InventoryDashboardRow>());

        var cut = Render<InventoryDashboard>();

        cut.Find("#createInventoryButton").TextContent.Should().Contain("Create Inventory");
    }

    private static InventoryDashboardRow SampleRow(string sku, int onHand = 0, int reserved = 0)
        => new(
            InventoryId: Guid.NewGuid(),
            Sku: sku,
            OnHandQuantity: onHand,
            ReservedQuantity: reserved,
            LastUpdatedUtc: new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc));
}
