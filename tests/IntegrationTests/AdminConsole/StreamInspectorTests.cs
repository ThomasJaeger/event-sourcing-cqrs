extern alias AdminConsoleHost;
using AdminConsoleHost::EventSourcingCqrs.Hosts.AdminConsole.Browser;
using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.Domain.Sales;
using EventSourcingCqrs.Domain.Sales.Events;
using EventSourcingCqrs.Domain.SharedKernel;
using EventSourcingCqrs.IntegrationTests.Commands;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EventSourcingCqrs.IntegrationTests.AdminConsole;

// Phase 12, the Event Store Browser seam (RED). IStreamInspector inspects a single aggregate stream for
// operator display across four outcomes. These specs boot the real AdminConsole host over migrated
// Testcontainers Postgres (the admit fixture) and resolve the seam from it, the same composition shape the
// throughput-rebuild test uses. The seam is declared but has no implementation registered, so the resolve
// throws here: the RED. The seed, the inspect, and the assertions below are the GREEN-ready shape the
// seam's implementation and its host registration will satisfy.
public class StreamInspectorTests : IClassFixture<AdminConsoleAdmitFixture>
{
    private static readonly DateTime SeedTime = new(2026, 5, 14, 12, 0, 0, DateTimeKind.Utc);

    private readonly AdminConsoleAdmitFixture _fixture;

    public StreamInspectorTests(AdminConsoleAdmitFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Inspecting_a_seeded_aggregate_stream_returns_found_with_the_events_shaped_for_display()
    {
        using var scope = _fixture.Factory.Services.CreateScope();
        var sp = scope.ServiceProvider;
        var inspector = sp.GetRequiredService<IStreamInspector>();
        var eventStore = sp.GetRequiredService<IEventStore>();

        var orderId = Guid.NewGuid();
        var stream = StreamId.ForAggregate<Order>(WellKnownTenants.Default, orderId);
        await EventStoreSeed.AppendAsync(
            eventStore,
            stream,
            [
                new OrderDrafted(orderId, Guid.NewGuid(), SeedTime),
                new OrderPlaced(orderId, Guid.NewGuid(), new Money(20m, Currency.USD), SeedTime.AddHours(1)),
            ],
            CancellationToken.None);

        var result = await inspector.InspectStreamAsync(stream.Value, CancellationToken.None);

        result.Outcome.Should().Be(StreamInspectionOutcome.Found);
        result.Events.Should().HaveCount(2);
        var first = result.Events[0];
        first.StreamVersion.Should().Be(1);
        first.EventType.Should().Be(nameof(OrderDrafted));
        first.OccurredUtc.Kind.Should().Be(DateTimeKind.Utc);
        first.OccurredUtc.Should().NotBe(default);
        first.GlobalPosition.Should().BeGreaterThan(0);
        // The payload re-serializes the concrete event, not the IDomainEvent marker: the order id is in
        // the JSON, so a polymorphic-default empty object would fail this assertion.
        first.PayloadJson.Should().Contain(orderId.ToString());
        first.Metadata.Should().NotBeNull();
    }

    [Fact]
    public async Task Inspecting_a_valid_aggregate_stream_id_with_no_events_returns_empty()
    {
        using var scope = _fixture.Factory.Services.CreateScope();
        var sp = scope.ServiceProvider;
        var inspector = sp.GetRequiredService<IStreamInspector>();

        var stream = StreamId.ForAggregate<Order>(WellKnownTenants.Default, Guid.NewGuid());

        var result = await inspector.InspectStreamAsync(stream.Value, CancellationToken.None);

        result.Outcome.Should().Be(StreamInspectionOutcome.Empty);
        result.Events.Should().BeEmpty();
    }

    [Fact]
    public async Task Inspecting_a_process_manager_stream_id_returns_process_manager_unsupported()
    {
        using var scope = _fixture.Factory.Services.CreateScope();
        var sp = scope.ServiceProvider;
        var inspector = sp.GetRequiredService<IStreamInspector>();

        var pmStream = StreamId.ForProcessManager(
            StreamPrefixes.OrderFulfillmentPm, WellKnownTenants.Default, Guid.NewGuid());

        var result = await inspector.InspectStreamAsync(pmStream.Value, CancellationToken.None);

        result.Outcome.Should().Be(StreamInspectionOutcome.ProcessManagerUnsupported);
        result.Events.Should().BeEmpty();
    }

    [Fact]
    public async Task Inspecting_a_malformed_input_returns_invalid_format()
    {
        using var scope = _fixture.Factory.Services.CreateScope();
        var sp = scope.ServiceProvider;
        var inspector = sp.GetRequiredService<IStreamInspector>();

        var result = await inspector.InspectStreamAsync("not-a-valid-stream-id", CancellationToken.None);

        result.Outcome.Should().Be(StreamInspectionOutcome.InvalidFormat);
        result.Events.Should().BeEmpty();
    }
}
