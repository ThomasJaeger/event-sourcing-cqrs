using System.Net;
using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.Domain.Sales;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EventSourcingCqrs.IntegrationTests.Commands.Sales;

public class SetOrderShippingAddressTests : IClassFixture<ApiFixture>
{
    private readonly ApiFixture _fixture;

    public SetOrderShippingAddressTests(ApiFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Posting_set_shipping_address_appends_shipping_address_set_event()
    {
        var client = _fixture.Factory.CreateClient();
        var orderId = Guid.NewGuid();

        await OrderSetup.DraftAsync(client, orderId);

        var response = await client.PostCommandAsync(
            "SetOrderShippingAddress",
            new
            {
                orderId,
                shippingAddress = new
                {
                    street = "100 Main St",
                    city = "Reno",
                    postalCode = "89501",
                    country = "US",
                },
            },
            idempotencyKey: Guid.NewGuid().ToString());
        response.StatusCode.Should().Be(HttpStatusCode.Accepted);

        var eventStore = _fixture.Factory.Services.GetRequiredService<IEventStore>();
        var envelopes = await eventStore.ReadStreamAsync(
            StreamId.ForAggregate<Order>(orderId), fromVersion: 0);
        envelopes.Should().HaveCount(2);
        envelopes[1].EventType.Should().Be("ShippingAddressSet");
    }

    [Fact]
    public async Task Posting_set_shipping_address_on_placed_order_returns_422()
    {
        var client = _fixture.Factory.CreateClient();
        var orderId = Guid.NewGuid();

        await OrderSetup.PlaceOrderAsync(client, orderId);

        var response = await client.PostCommandAsync(
            "SetOrderShippingAddress",
            new
            {
                orderId,
                shippingAddress = new
                {
                    street = "200 New St",
                    city = "Reno",
                    postalCode = "89502",
                    country = "US",
                },
            },
            idempotencyKey: Guid.NewGuid().ToString());
        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
    }
}
