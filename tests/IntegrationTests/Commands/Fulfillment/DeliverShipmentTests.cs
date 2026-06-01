using System.Net;
using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.Domain.Fulfillment;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EventSourcingCqrs.IntegrationTests.Commands.Fulfillment;

public class DeliverShipmentTests : IClassFixture<ApiFixture>
{
    private readonly ApiFixture _fixture;

    public DeliverShipmentTests(ApiFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Posting_deliver_shipment_on_dispatched_shipment_appends_shipment_delivered_event()
    {
        var client = _fixture.Factory.CreateClient();
        var shipmentId = Guid.NewGuid();
        var eventStore = _fixture.Factory.Services.GetRequiredService<IEventStore>();

        await ShipmentSeed.DispatchedAsync(eventStore, shipmentId);

        var response = await client.PostCommandAsync(
            "DeliverShipment",
            new { shipmentId },
            idempotencyKey: Guid.NewGuid().ToString());
        response.StatusCode.Should().Be(HttpStatusCode.Accepted);

        var envelopes = await eventStore.ReadStreamAsync(
            StreamId.ForAggregate<Shipment>(WellKnownTenants.Default, shipmentId), fromVersion: 0);
        envelopes.Should().HaveCount(3);
        envelopes[2].EventType.Should().Be("ShipmentDelivered");
    }

    [Fact]
    public async Task Posting_deliver_shipment_on_scheduled_shipment_returns_422()
    {
        var client = _fixture.Factory.CreateClient();
        var shipmentId = Guid.NewGuid();
        var eventStore = _fixture.Factory.Services.GetRequiredService<IEventStore>();

        await ShipmentSeed.ScheduledAsync(eventStore, shipmentId);

        var response = await client.PostCommandAsync(
            "DeliverShipment",
            new { shipmentId },
            idempotencyKey: Guid.NewGuid().ToString());

        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
    }

    [Fact]
    public async Task Posting_deliver_shipment_on_unknown_shipment_returns_404()
    {
        var client = _fixture.Factory.CreateClient();

        var response = await client.PostCommandAsync(
            "DeliverShipment",
            new { shipmentId = Guid.NewGuid() },
            idempotencyKey: Guid.NewGuid().ToString());

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}
