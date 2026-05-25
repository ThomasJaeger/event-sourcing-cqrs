using System.Net;
using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.Domain.Sales;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EventSourcingCqrs.IntegrationTests.Commands.Sales;

public class PlaceOrderTests : IClassFixture<ApiFixture>
{
    private readonly ApiFixture _fixture;

    public PlaceOrderTests(ApiFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Posting_place_order_appends_order_placed_event()
    {
        var client = _fixture.Factory.CreateClient();
        var orderId = Guid.NewGuid();

        await OrderSetup.DraftWithLineAndAddressAsync(client, orderId);

        var response = await client.PostCommandAsync(
            "PlaceOrder",
            new { orderId },
            idempotencyKey: Guid.NewGuid().ToString());
        response.StatusCode.Should().Be(HttpStatusCode.Accepted);

        var eventStore = _fixture.Factory.Services.GetRequiredService<IEventStore>();
        var envelopes = await eventStore.ReadStreamAsync(
            StreamId.ForAggregate<Order>(orderId), fromVersion: 0);
        envelopes.Should().HaveCount(4);
        envelopes[3].EventType.Should().Be("OrderPlaced");
    }

    [Fact]
    public async Task Posting_place_order_with_no_lines_returns_422()
    {
        var client = _fixture.Factory.CreateClient();
        var orderId = Guid.NewGuid();

        await OrderSetup.DraftAsync(client, orderId);
        await client.PostCommandAsync(
            "SetOrderShippingAddress",
            new
            {
                orderId,
                shippingAddress = new
                {
                    street = "1 Test St",
                    city = "Reno",
                    postalCode = "89501",
                    country = "US",
                },
            },
            idempotencyKey: Guid.NewGuid().ToString());

        var response = await client.PostCommandAsync(
            "PlaceOrder",
            new { orderId },
            idempotencyKey: Guid.NewGuid().ToString());
        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
    }

    [Fact]
    public async Task Posting_place_order_with_no_address_returns_422()
    {
        var client = _fixture.Factory.CreateClient();
        var orderId = Guid.NewGuid();

        await OrderSetup.DraftWithLineAsync(client, orderId, Guid.NewGuid());

        var response = await client.PostCommandAsync(
            "PlaceOrder",
            new { orderId },
            idempotencyKey: Guid.NewGuid().ToString());
        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
    }
}
