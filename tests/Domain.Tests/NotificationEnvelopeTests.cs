using System.Text.Json;
using EventSourcingCqrs.Domain.Abstractions;
using FluentAssertions;
using Xunit;

namespace EventSourcingCqrs.Domain.Tests;

public class NotificationEnvelopeTests
{
    [Fact]
    public void NotificationEnvelope_serializes_and_deserializes_round_trip()
    {
        var envelope = new NotificationEnvelope(
            ProjectionName: "OrderDetail",
            ResourceId: "order-123",
            EventName: "OrderShipped",
            Widgets: new[] { "status", "timeline" });

        var json = JsonSerializer.Serialize(envelope);
        var roundTripped = JsonSerializer.Deserialize<NotificationEnvelope>(json);

        roundTripped.Should().BeEquivalentTo(envelope);
    }
}
