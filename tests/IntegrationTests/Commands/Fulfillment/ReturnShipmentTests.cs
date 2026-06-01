using System.Net;
using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.Domain.Fulfillment;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EventSourcingCqrs.IntegrationTests.Commands.Fulfillment;

public class ReturnShipmentTests : IClassFixture<ApiFixture>
{
    private readonly ApiFixture _fixture;

    public ReturnShipmentTests(ApiFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Posting_return_shipment_on_delivered_shipment_appends_shipment_returned_event()
    {
        var client = _fixture.Factory.CreateClient();
        var shipmentId = Guid.NewGuid();
        var eventStore = _fixture.Factory.Services.GetRequiredService<IEventStore>();

        await ShipmentSeed.DeliveredAsync(eventStore, shipmentId);

        var response = await client.PostCommandAsync(
            "ReturnShipment",
            new { shipmentId, reason = "damaged on arrival" },
            idempotencyKey: Guid.NewGuid().ToString());
        response.StatusCode.Should().Be(HttpStatusCode.Accepted);

        var envelopes = await eventStore.ReadStreamAsync(
            StreamId.ForAggregate<Shipment>(WellKnownTenants.Default, shipmentId), fromVersion: 0);
        envelopes.Should().HaveCount(4);
        envelopes[3].EventType.Should().Be("ShipmentReturned");
    }

    [Fact]
    public async Task Posting_return_shipment_on_dispatched_shipment_returns_422()
    {
        var client = _fixture.Factory.CreateClient();
        var shipmentId = Guid.NewGuid();
        var eventStore = _fixture.Factory.Services.GetRequiredService<IEventStore>();

        await ShipmentSeed.DispatchedAsync(eventStore, shipmentId);

        var response = await client.PostCommandAsync(
            "ReturnShipment",
            new { shipmentId, reason = "any" },
            idempotencyKey: Guid.NewGuid().ToString());

        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
    }

    [Fact]
    public async Task Posting_return_shipment_on_unknown_shipment_returns_404()
    {
        var client = _fixture.Factory.CreateClient();

        var response = await client.PostCommandAsync(
            "ReturnShipment",
            new { shipmentId = Guid.NewGuid(), reason = "any" },
            idempotencyKey: Guid.NewGuid().ToString());

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}
