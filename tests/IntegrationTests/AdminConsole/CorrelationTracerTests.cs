extern alias AdminConsoleHost;
using System.Text.Json;
using AdminConsoleHost::EventSourcingCqrs.Hosts.AdminConsole.Tracer;
using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.Domain.Sales;
using EventSourcingCqrs.Domain.Sales.Events;
using EventSourcingCqrs.Domain.SharedKernel;
using EventSourcingCqrs.Infrastructure.EventStore.Postgres;
using EventSourcingCqrs.Infrastructure.Versioning;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace EventSourcingCqrs.IntegrationTests.AdminConsole;

// Phase 12, the Correlation-ID Tracer seam (RED). ICorrelationTracer traces one workflow across every stream
// and every tenant carrying its correlation id, and these specs pin its three outcomes: InvalidFormat for a
// string that is not a Guid and for Guid.Empty, Empty for a well-formed correlation matching nothing, and
// Found for a seeded workflow, whose rows carry their stream, their family, and their typed metadata. They
// boot the real AdminConsole host over migrated Testcontainers Postgres (the admit fixture) and resolve the
// seam from it, the composition shape StreamInspectorTests uses.
//
// The seam has no implementation registered in the host, so the resolve throws here: the RED. The seeds and
// the assertions below are the GREEN-ready shape the seam and its registration will satisfy.
//
// Truncation is absent by decision, not by oversight. The seam's cap is 500, and seeding past it is not a
// reasonable integration seed. Commit 1's adapter sweep already pins the cap-plus-one mechanics against the
// real database at a small cap; what remains for the seam is that it hands its own constant to the port, and
// that is covered once the implementation exists to construct with a smaller one. The Found case below pins
// the flag's flow through the seam.
public class CorrelationTracerTests : IClassFixture<AdminConsoleAdmitFixture>
{
    private static readonly DateTime SeedTime = new(2026, 6, 2, 12, 0, 0, DateTimeKind.Utc);

    private static readonly TenantId OtherTenant =
        TenantId.From(Guid.Parse("33333333-3333-3333-3333-333333333333"));

    private readonly AdminConsoleAdmitFixture _fixture;

    public CorrelationTracerTests(AdminConsoleAdmitFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Tracing_an_input_that_is_not_a_guid_returns_invalid_format()
    {
        using var scope = _fixture.Factory.Services.CreateScope();
        var tracer = scope.ServiceProvider.GetRequiredService<ICorrelationTracer>();

        var view = await tracer.TraceCorrelationAsync("not-a-correlation-id", CancellationToken.None);

        view.Outcome.Should().Be(CorrelationTraceOutcome.InvalidFormat);
        view.Events.Should().BeEmpty();
    }

    [Fact]
    public async Task Tracing_the_empty_guid_returns_sentinel_refused()
    {
        using var scope = _fixture.Factory.Services.CreateScope();
        var tracer = scope.ServiceProvider.GetRequiredService<ICorrelationTracer>();

        var view = await tracer.TraceCorrelationAsync(Guid.Empty.ToString(), CancellationToken.None);

        // Guid.Empty parses, so it is not a format problem. It is refused as its own outcome because it is
        // a sentinel rather than a workflow, and post-ADR 0042 it matches only the process-manager rows
        // written before that fix. The page owns the wording for each refusal.
        view.Outcome.Should().Be(CorrelationTraceOutcome.SentinelRefused);
        view.Events.Should().BeEmpty();
    }

    [Fact]
    public async Task Tracing_a_well_formed_correlation_matching_no_rows_returns_empty()
    {
        using var scope = _fixture.Factory.Services.CreateScope();
        var tracer = scope.ServiceProvider.GetRequiredService<ICorrelationTracer>();

        var view = await tracer.TraceCorrelationAsync(Guid.NewGuid().ToString(), CancellationToken.None);

        view.Outcome.Should().Be(CorrelationTraceOutcome.Empty);
        view.Events.Should().BeEmpty();
        view.Truncated.Should().BeFalse();
    }

    [Fact]
    public async Task Tracing_a_seeded_workflow_returns_its_rows_across_streams_ordered_by_global_position()
    {
        using var scope = _fixture.Factory.Services.CreateScope();
        var sp = scope.ServiceProvider;
        var tracer = sp.GetRequiredService<ICorrelationTracer>();
        var eventStore = sp.GetRequiredService<IEventStore>();

        var correlationId = Guid.NewGuid();
        var orderId = Guid.NewGuid();
        var otherOrderId = Guid.NewGuid();
        var orderStream = StreamId.ForAggregate<Order>(WellKnownTenants.Default, orderId);
        var otherStream = StreamId.ForAggregate<Order>(WellKnownTenants.Default, otherOrderId);

        await SeedAggregateAsync(eventStore, orderStream, correlationId, WellKnownTenants.Default,
            [
                new OrderDrafted(orderId, Guid.NewGuid(), SeedTime, "web"),
                new OrderPlaced(orderId, Guid.NewGuid(), new Money(20m, Currency.USD), SeedTime.AddHours(1)),
            ]);
        await SeedAggregateAsync(eventStore, otherStream, correlationId, WellKnownTenants.Default,
            [new OrderDrafted(otherOrderId, Guid.NewGuid(), SeedTime.AddHours(2), "web")]);

        var view = await tracer.TraceCorrelationAsync(correlationId.ToString(), CancellationToken.None);

        view.Outcome.Should().Be(CorrelationTraceOutcome.Found);
        view.Truncated.Should().BeFalse();
        view.Events.Should().HaveCount(3);
        view.Events.Select(e => e.GlobalPosition).Should().BeInAscendingOrder();
        view.Events.Select(e => e.StreamId).Should()
            .Equal(orderStream.Value, orderStream.Value, otherStream.Value);
        view.Events.Select(e => e.EventType).Should()
            .Equal(nameof(OrderDrafted), nameof(OrderPlaced), nameof(OrderDrafted));
        view.Events.Should().AllSatisfy(e =>
        {
            e.Metadata.CorrelationId.Should().Be(correlationId);
            e.Metadata.CausationId.Should().NotBe(Guid.Empty);
            e.Metadata.Source.Should().Be(SeedSource);
            e.Metadata.Tenant.Should().Be(WellKnownTenants.Default);
            e.OccurredUtc.Kind.Should().Be(DateTimeKind.Utc);
        });
        // The payload is the stored text, so the concrete event's properties are in it without any type
        // having been resolved on the way out.
        view.Events[0].PayloadJson.Should().Contain(orderId.ToString());
    }

    [Fact]
    public async Task Tracing_a_workflow_with_a_process_manager_leg_classifies_each_rows_family()
    {
        using var scope = _fixture.Factory.Services.CreateScope();
        var sp = scope.ServiceProvider;
        var tracer = sp.GetRequiredService<ICorrelationTracer>();
        var eventStore = sp.GetRequiredService<IEventStore>();
        var jsonOptions = sp.GetRequiredService<JsonSerializerOptions>();

        var correlationId = Guid.NewGuid();
        var orderId = Guid.NewGuid();
        var orderStream = StreamId.ForAggregate<Order>(WellKnownTenants.Default, orderId);
        var pmStream = StreamId.ForProcessManager(
            StreamPrefixes.OrderFulfillmentPm, WellKnownTenants.Default, orderId);

        await SeedAggregateAsync(eventStore, orderStream, correlationId, WellKnownTenants.Default,
            [new OrderPlaced(orderId, Guid.NewGuid(), new Money(20m, Currency.USD), SeedTime)]);
        await SeedProcessManagerAsync(pmStream, correlationId, WellKnownTenants.Default, jsonOptions);

        var view = await tracer.TraceCorrelationAsync(correlationId.ToString(), CancellationToken.None);

        // The one-table join ADR 0013 promised: the aggregate row and the process manager's own row come
        // back from one read, and the seam tells the operator which is which.
        view.Outcome.Should().Be(CorrelationTraceOutcome.Found);
        view.Events.Should().HaveCount(2);
        view.Events.Select(e => e.Kind).Should()
            .Equal(TracedEventKind.Aggregate, TracedEventKind.ProcessManager);
        view.Events.Select(e => e.StreamId).Should().Equal(orderStream.Value, pmStream.Value);
        view.Events[1].EventType.Should().Be(nameof(TracedPmEvent));
    }

    [Fact]
    public async Task Tracing_a_workflow_that_crosses_tenants_reports_each_rows_own_tenant()
    {
        using var scope = _fixture.Factory.Services.CreateScope();
        var sp = scope.ServiceProvider;
        var tracer = sp.GetRequiredService<ICorrelationTracer>();
        var eventStore = sp.GetRequiredService<IEventStore>();

        var correlationId = Guid.NewGuid();
        var defaultOrderId = Guid.NewGuid();
        var otherOrderId = Guid.NewGuid();
        var defaultStream = StreamId.ForAggregate<Order>(WellKnownTenants.Default, defaultOrderId);
        var otherStream = StreamId.ForAggregate<Order>(OtherTenant, otherOrderId);

        await SeedAggregateAsync(eventStore, defaultStream, correlationId, WellKnownTenants.Default,
            [new OrderDrafted(defaultOrderId, Guid.NewGuid(), SeedTime, "web")]);
        await SeedAggregateAsync(eventStore, otherStream, correlationId, OtherTenant,
            [new OrderDrafted(otherOrderId, Guid.NewGuid(), SeedTime.AddHours(1), "web")]);

        var view = await tracer.TraceCorrelationAsync(correlationId.ToString(), CancellationToken.None);

        // The read carries no tenant predicate by decision. A chain crossing tenants is the isolation defect
        // the tool exists to reveal, so both rows come back and each reports the tenant it was written under.
        view.Outcome.Should().Be(CorrelationTraceOutcome.Found);
        view.Events.Should().HaveCount(2);
        view.Events.Select(e => e.Metadata.Tenant).Should().Equal(WellKnownTenants.Default, OtherTenant);
    }

    [Fact]
    public async Task Tracing_past_the_cap_returns_the_earliest_rows_and_flags_truncation()
    {
        using var scope = _fixture.Factory.Services.CreateScope();
        var sp = scope.ServiceProvider;
        var eventStore = sp.GetRequiredService<IEventStore>();

        // The seam's own cap is 500, and seeding past it is not a reasonable integration seed. Construct the
        // seam over the host's real port with a cap of 2 instead: the port's cap-plus-one mechanics are
        // pinned at the adapter level, and what this covers is the seam handing its cap down and surfacing
        // the flag the port returns.
        var tracer = new CorrelationTracer(sp.GetRequiredService<ICorrelationTraceReader>(), maxRows: 2);

        var correlationId = Guid.NewGuid();
        var orderId = Guid.NewGuid();
        var stream = StreamId.ForAggregate<Order>(WellKnownTenants.Default, orderId);
        await SeedAggregateAsync(eventStore, stream, correlationId, WellKnownTenants.Default,
            [
                new OrderDrafted(orderId, Guid.NewGuid(), SeedTime, "web"),
                new OrderPlaced(orderId, Guid.NewGuid(), new Money(20m, Currency.USD), SeedTime.AddHours(1)),
                new OrderCancelled(orderId, "operator request", Guid.NewGuid(), SeedTime.AddHours(2)),
            ]);

        var view = await tracer.TraceCorrelationAsync(correlationId.ToString(), CancellationToken.None);

        view.Outcome.Should().Be(CorrelationTraceOutcome.Found);
        view.Events.Should().HaveCount(2);
        view.Truncated.Should().BeTrue();
        view.Events.Select(e => e.EventType).Should().Equal(nameof(OrderDrafted), nameof(OrderPlaced));
        view.Events.Select(e => e.GlobalPosition).Should().BeInAscendingOrder();
    }

    private const string SeedSource = "correlation-tracer-test-seed";

    // Aggregate rows seed through the host-resolved event store, so the host's own registries and JSON
    // options write them. EventStoreSeed cannot serve here: its BuildEnvelope is private and stamps a fresh
    // Guid.NewGuid() correlation per event, and a trace needs one chosen correlation across every row.
    private static async Task SeedAggregateAsync(
        IEventStore eventStore,
        StreamId streamId,
        Guid correlationId,
        TenantId tenant,
        IReadOnlyList<IDomainEvent> events)
    {
        var envelopes = events
            .Select((payload, i) => BuildEnvelope(streamId, i + 1, payload, correlationId, tenant))
            .ToList();
        await eventStore.AppendAsync(streamId, expectedVersion: 0, envelopes, CancellationToken.None);
    }

    // Process-manager rows cannot seed through the host-resolved store. The AdminConsole composes no
    // IProcessManagerEventTypeProvider, by design and permanently (ADR 0043), so its PM registry is empty and
    // the append would throw at NameFor. Build the adapter directly against the fixture's database with a PM
    // registry holding this suite's own PM event type, the hand-roll shape PostgresEventStore_Tenant_Tests
    // established. The host's JsonSerializerOptions come along, so the row is written exactly as the host
    // would write it.
    private async Task SeedProcessManagerAsync(
        StreamId pmStream,
        Guid correlationId,
        TenantId tenant,
        JsonSerializerOptions jsonOptions)
    {
        await using var dataSource = NpgsqlDataSource.Create(_fixture.ConnectionString);
        var store = new PostgresEventStore(
            new NpgsqlConnectionFactory(dataSource),
            new EventTypeRegistry(),
            new ProcessManagerEventTypeRegistry().Register<TracedPmEvent>(),
            jsonOptions,
            new EventUpcasterPipeline(new EventTypeRegistry(), []));

        var eventId = Guid.NewGuid();
        var envelope = new ProcessManagerEventEnvelope(
            StreamId: pmStream,
            StreamVersion: 1,
            EventId: eventId,
            EventType: nameof(TracedPmEvent),
            EventVersion: 1,
            Payload: new TracedPmEvent(1),
            Metadata: BuildMetadata(eventId, correlationId, tenant, SeedTime),
            OccurredUtc: SeedTime,
            GlobalPosition: 0);

        await store.AppendProcessManagerEventsAsync(
            pmStream, expectedVersion: 0, [envelope], CancellationToken.None);
    }

    private static EventEnvelope BuildEnvelope(
        StreamId streamId, int streamVersion, IDomainEvent payload, Guid correlationId, TenantId tenant)
    {
        var eventId = Guid.NewGuid();
        var occurredUtc = SeedTime.AddMinutes(streamVersion);
        return new EventEnvelope(
            StreamId: streamId,
            StreamVersion: streamVersion,
            EventId: eventId,
            EventType: payload.GetType().Name,
            EventVersion: 1,
            Payload: payload,
            Metadata: BuildMetadata(eventId, correlationId, tenant, occurredUtc),
            OccurredUtc: occurredUtc,
            GlobalPosition: 0);
    }

    private static EventMetadata BuildMetadata(
        Guid eventId, Guid correlationId, TenantId tenant, DateTime occurredUtc)
        => new(
            EventId: eventId,
            CorrelationId: correlationId,
            CausationId: Guid.NewGuid(),
            ActorId: Guid.NewGuid(),
            Source: SeedSource,
            OccurredUtc: occurredUtc,
            Tenant: tenant);

    private sealed record TracedPmEvent(int Step) : IProcessManagerEvent;
}
