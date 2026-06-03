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
            Widgets: new[] { "status", "timeline" },
            Tenant: WellKnownTenants.Default);

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

    [Fact]
    public void SerializerOptions_serializes_a_tenant_id_as_a_flat_scalar_and_round_trips_it()
    {
        // The broadcast group is built from the envelope tenant, so the tenant
        // must cross the projection_committed wire as a parseable flat scalar,
        // the same shape the events-table metadata tenant uses. A non-default
        // tenant proves the value survives rather than defaulting.
        var tenant = TenantId.From(Guid.Parse("55555555-5555-5555-5555-555555555555"));

        var json = JsonSerializer.Serialize(tenant, NotificationContract.SerializerOptions);

        json.Should().Be("\"55555555-5555-5555-5555-555555555555\"");

        var roundTripped = JsonSerializer.Deserialize<TenantId>(
            json, NotificationContract.SerializerOptions);

        roundTripped.Should().Be(tenant);
    }
}
