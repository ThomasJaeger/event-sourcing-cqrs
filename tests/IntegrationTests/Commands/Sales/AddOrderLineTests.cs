using System.Net;
using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.Domain.Sales;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EventSourcingCqrs.IntegrationTests.Commands.Sales;

public class AddOrderLineTests : IClassFixture<ApiFixture>
{
    private readonly ApiFixture _fixture;

    public AddOrderLineTests(ApiFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Posting_add_order_line_on_drafted_order_appends_order_line_added_event()
    {
        var client = _fixture.Factory.CreateClient();
        var orderId = Guid.NewGuid();
        var lineId = Guid.NewGuid();

        await OrderSetup.DraftAsync(client, orderId);

        var response = await client.PostCommandAsync(
            "AddOrderLine",
            new
            {
                orderId,
                lineId,
                sku = "SKU-001",
                quantity = 2,
                unitPrice = new { amount = 19.99m, currency = new { code = "USD" } },
            },
            idempotencyKey: Guid.NewGuid().ToString());
        response.StatusCode.Should().Be(HttpStatusCode.Accepted);

        var eventStore = _fixture.Factory.Services.GetRequiredService<IEventStore>();
        var envelopes = await eventStore.ReadStreamAsync(
            StreamId.ForAggregate<Order>(WellKnownTenants.Default, orderId), fromVersion: 0);
        envelopes.Should().HaveCount(2);
        envelopes[1].EventType.Should().Be("OrderLineAdded");
    }

    [Fact]
    public async Task Posting_add_order_line_to_placed_order_returns_422()
    {
        var client = _fixture.Factory.CreateClient();
        var orderId = Guid.NewGuid();

        await OrderSetup.PlaceOrderAsync(client, orderId);

        var response = await client.PostCommandAsync(
            "AddOrderLine",
            new
            {
                orderId,
                lineId = Guid.NewGuid(),
                sku = "SKU-002",
                quantity = 1,
                unitPrice = new { amount = 9.99m, currency = new { code = "USD" } },
            },
            idempotencyKey: Guid.NewGuid().ToString());

        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
    }

    [Fact]
    public async Task Posting_add_order_line_to_unknown_order_returns_404()
    {
        var client = _fixture.Factory.CreateClient();

        var response = await client.PostCommandAsync(
            "AddOrderLine",
            new
            {
                orderId = Guid.NewGuid(),
                lineId = Guid.NewGuid(),
                sku = "SKU-003",
                quantity = 1,
                unitPrice = new { amount = 1.00m, currency = new { code = "USD" } },
            },
            idempotencyKey: Guid.NewGuid().ToString());

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}
