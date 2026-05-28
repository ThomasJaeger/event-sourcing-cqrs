using System.Text.Json;
using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.Infrastructure.SignalR;
using FluentAssertions;
using Xunit;

namespace EventSourcingCqrs.Infrastructure.Tests.SignalR;

public class NotificationContractTests
{
    [Fact]
    public void SerializerOptions_round_trips_every_routing_field()
    {
        var original = new NotificationEnvelope(
            ProjectionName: "order-detail",
            ResourceId: "11111111-2222-3333-4444-555555555555",
            EventName: "OrderShipped",
            Widgets: new[] { "status", "timeline" });

        var json = JsonSerializer.Serialize(original, NotificationContract.SerializerOptions);
        var roundTripped = JsonSerializer.Deserialize<NotificationEnvelope>(
            json, NotificationContract.SerializerOptions);

        // The routing fields are what a casing mismatch silently nulls out while
        // leaving Widgets populated; assert each one survives, not just Widgets.
        roundTripped.Should().NotBeNull();
        roundTripped!.ProjectionName.Should().Be("order-detail");
        roundTripped.ResourceId.Should().Be("11111111-2222-3333-4444-555555555555");
        roundTripped.EventName.Should().Be("OrderShipped");
        roundTripped.Widgets.Should().Equal("status", "timeline");
    }
}
