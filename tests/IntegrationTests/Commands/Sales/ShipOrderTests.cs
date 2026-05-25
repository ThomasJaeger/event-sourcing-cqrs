using System.Net;
using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.Domain.Sales;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EventSourcingCqrs.IntegrationTests.Commands.Sales;

public class ShipOrderTests : IClassFixture<ApiFixture>
{
    private readonly ApiFixture _fixture;

    public ShipOrderTests(ApiFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Posting_ship_order_on_placed_order_appends_order_shipped_event()
    {
        var client = _fixture.Factory.CreateClient();
        var orderId = Guid.NewGuid();

        await OrderSetup.PlaceOrderAsync(client, orderId);

        var response = await client.PostCommandAsync(
            "ShipOrder",
            new { orderId, carrier = "UPS", trackingNumber = "1Z999AA10123456784" },
            idempotencyKey: Guid.NewGuid().ToString());
        response.StatusCode.Should().Be(HttpStatusCode.Accepted);

        var eventStore = _fixture.Factory.Services.GetRequiredService<IEventStore>();
        var envelopes = await eventStore.ReadStreamAsync(
            StreamId.ForAggregate<Order>(orderId), fromVersion: 0);
        envelopes.Should().HaveCount(5);
        envelopes[4].EventType.Should().Be("OrderShipped");
    }

    [Fact]
    public async Task Posting_ship_order_on_drafted_order_returns_422()
    {
        var client = _fixture.Factory.CreateClient();
        var orderId = Guid.NewGuid();

        await OrderSetup.DraftAsync(client, orderId);

        var response = await client.PostCommandAsync(
            "ShipOrder",
            new { orderId, carrier = "UPS", trackingNumber = "TRK-X" },
            idempotencyKey: Guid.NewGuid().ToString());
        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
    }
}
