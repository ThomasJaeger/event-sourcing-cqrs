using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.Domain.Fulfillment;
using EventSourcingCqrs.Domain.Fulfillment.Events;
using EventSourcingCqrs.Domain.Sales;
using EventSourcingCqrs.Domain.Sales.Events;
using EventSourcingCqrs.Domain.SharedKernel;
using EventSourcingCqrs.Hosts.Workers;
using EventSourcingCqrs.ProcessManagers.OrderFulfillment;
using EventSourcingCqrs.ProcessManagers.Returns;
using EventSourcingCqrs.TestInfrastructure;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace EventSourcingCqrs.Workers.Tests;

// End-to-end through the Workers host: the outbox dispatcher's new
// IProcessManagerHandler<T> routing (commit 27, scope-per-message) drives both
// process managers through the real pipeline. The harness tests short-circuit the
// pipeline; these exercise the registered handlers, the scope-per-message
// resolution of scoped PM handlers and their scoped repositories, and the
// command/event/outbox cascade end-to-end. Event timestamps are now-based so the
// AwaitingPayment timeout (fire-at = OccurredUtc + the configured window) stays in
// the future and does not race the cascade.
public class WorkersHostProcessManagerTests : IClassFixture<PostgresFixture>
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(50);
    private static readonly TimeSpan PollBudget = TimeSpan.FromSeconds(20);

    private readonly PostgresFixture _fixture;

    public WorkersHostProcessManagerTests(PostgresFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task OrderFulfillment_PM_compensates_an_uninventoried_order_to_cancelled()
    {
        var connStr = await _fixture.CreateMigratedDatabaseAsync();
        using var host = WorkersHostFactory.Build(connStr, connStr);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await host.StartAsync(cts.Token);
        try
        {
            var now = DateTime.UtcNow;
            var orderId = Guid.NewGuid();
            var customerId = Guid.NewGuid();
            var lineId = Guid.NewGuid();
            var stream = StreamId.ForAggregate<Order>(WellKnownTenants.Default, orderId);

            // No inventory exists for SKU-1, so the OF PM authorizes payment, fails
            // to reserve, then voids and cancels end-to-end through the real
            // dispatcher routing and command pipeline.
            var eventStore = host.Services.GetRequiredService<IEventStore>();
            await eventStore.AppendAsync(stream, 0,
            [
                Envelope(stream, 1, new OrderDrafted(orderId, customerId, now), now),
                Envelope(stream, 2, new OrderLineAdded(orderId, lineId, "SKU-1", 2, new Money(10m, Currency.USD), now), now),
                Envelope(stream, 3, new ShippingAddressSet(orderId, Address(), now), now),
                Envelope(stream, 4, new OrderPlaced(orderId, customerId, new Money(20m, Currency.USD), now), now),
            ], cts.Token);

            var pm = await PollAsync(
                () => LoadPmAsync<OrderFulfillmentProcessManager>(
                    host, StreamPrefixes.OrderFulfillmentPm, orderId, sid => new(sid), cts.Token),
                p => p.State == OrderFulfillmentState.Cancelled,
                cts.Token);

            pm.Should().NotBeNull(
                "the OrderFulfillment PM should authorize, fail to reserve, void, and cancel end-to-end");
        }
        finally
        {
            await host.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Return_PM_goes_stuck_when_a_returned_sku_has_no_inventory_mapping()
    {
        var connStr = await _fixture.CreateMigratedDatabaseAsync();
        using var host = WorkersHostFactory.Build(connStr, connStr);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await host.StartAsync(cts.Token);
        try
        {
            var now = DateTime.UtcNow;
            var orderId = Guid.NewGuid();
            var shipmentId = Guid.NewGuid();
            var lines = new List<ShipmentLine> { new(orderId, Guid.NewGuid(), "SKU-UNMAPPED", 1) };
            var stream = StreamId.ForAggregate<Shipment>(WellKnownTenants.Default, shipmentId);

            // No SKU-to-inventory mapping exists, so the Return PM loads the
            // shipment, fails the lookup, and goes Stuck end-to-end.
            var eventStore = host.Services.GetRequiredService<IEventStore>();
            await eventStore.AppendAsync(stream, 0,
            [
                Envelope(stream, 1, new ShipmentScheduled(shipmentId, orderId, Address(), lines, now), now),
                Envelope(stream, 2, new ShipmentReturned(shipmentId, "Customer return", now), now),
            ], cts.Token);

            var pm = await PollAsync(
                () => LoadPmAsync<ReturnProcessManager>(
                    host, StreamPrefixes.ReturnPm, orderId, sid => new(sid), cts.Token),
                p => p.State == ReturnState.Stuck,
                cts.Token);

            pm.Should().NotBeNull(
                "the Return PM should load the shipment, fail the SKU lookup, and go Stuck end-to-end");
            pm!.StuckReason.Should().Contain("no inventory mapping");
        }
        finally
        {
            await host.StopAsync(CancellationToken.None);
        }
    }

    // Pins the metadata the OrderFulfillment PM writes on the outbox route. ADR 0013 promises PM
    // rows join the aggregate trace by correlation_id; that only holds if the PM's own events carry
    // the causing workflow's identity. OrderPlaced opens the workflow (the handler's
    // HandleAsync(EventContext<OrderPlaced>) overload), so it is the causing event for the PM's
    // first save, and every event the PM writes belongs to that event's correlation.
    [Fact]
    public async Task OrderFulfillment_PM_events_carry_the_causing_workflow_identity_on_the_outbox_route()
    {
        var connStr = await _fixture.CreateMigratedDatabaseAsync();
        using var host = WorkersHostFactory.Build(connStr, connStr);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await host.StartAsync(cts.Token);
        try
        {
            var now = DateTime.UtcNow;
            var orderId = Guid.NewGuid();
            var customerId = Guid.NewGuid();
            var lineId = Guid.NewGuid();
            var correlationId = Guid.NewGuid();
            var stream = StreamId.ForAggregate<Order>(WellKnownTenants.Default, orderId);

            // The same uninventoried-SKU arc, seeded under one chosen correlation id so the PM's
            // metadata has something to be asserted against. OrderPlaced is held so its EventId can
            // be compared to the causation the PM's first event should carry.
            var orderPlaced = Envelope(
                stream, 4, new OrderPlaced(orderId, customerId, new Money(20m, Currency.USD), now), now, correlationId);

            var eventStore = host.Services.GetRequiredService<IEventStore>();
            await eventStore.AppendAsync(stream, 0,
            [
                Envelope(stream, 1, new OrderDrafted(orderId, customerId, now), now, correlationId),
                Envelope(stream, 2, new OrderLineAdded(orderId, lineId, "SKU-1", 2, new Money(10m, Currency.USD), now), now, correlationId),
                Envelope(stream, 3, new ShippingAddressSet(orderId, Address(), now), now, correlationId),
                orderPlaced,
            ], cts.Token);

            await PollAsync(
                () => LoadPmAsync<OrderFulfillmentProcessManager>(
                    host, StreamPrefixes.OrderFulfillmentPm, orderId, sid => new(sid), cts.Token),
                p => p.State == OrderFulfillmentState.Cancelled,
                cts.Token);

            // Read the PM stream through IEventStore: IProcessManagerRepository rehydrates payloads
            // and discards the metadata this test is about.
            var pmStream = StreamId.ForProcessManager(
                StreamPrefixes.OrderFulfillmentPm, WellKnownTenants.Default, orderId);
            var pmEvents = await eventStore.ReadProcessManagerStreamAsync(pmStream, fromVersion: 0, cts.Token);

            pmEvents.Should().NotBeEmpty("the PM should have written its arc through the outbox route");

            var first = pmEvents[0].Metadata;
            first.CorrelationId.Should().Be(
                correlationId, "the PM's first event belongs to the workflow OrderPlaced started");
            first.CausationId.Should().Be(
                orderPlaced.EventId, "the PM's first event is caused by the OrderPlaced that triggered it");
            first.ActorId.Should().Be(SystemActors.OrderFulfillment.Id);
            first.Source.Should().Be(SystemActors.OrderFulfillment.ServiceName);

            pmEvents.Should().AllSatisfy(e =>
            {
                e.Metadata.CorrelationId.Should().Be(correlationId, "the whole PM arc is one workflow");
                e.Metadata.CausationId.Should().NotBe(Guid.Empty, "every PM event is caused by something");
                e.Metadata.ActorId.Should().Be(SystemActors.OrderFulfillment.Id);
                e.Metadata.Source.Should().Be(SystemActors.OrderFulfillment.ServiceName);
            });

            // Within one save batch, every event after the first is caused by the prior event's
            // EventId (EventMetadata.ForCausedEvent). The cancellation branch emits its events in a
            // single batch, so the PM's last event chains off its predecessor.
            pmEvents[^1].Metadata.CausationId.Should().Be(
                pmEvents[^2].Metadata.EventId,
                "events after the first in one save batch chain off the prior event");
        }
        finally
        {
            await host.StopAsync(CancellationToken.None);
        }
    }

    private static async Task<TPm?> LoadPmAsync<TPm>(
        IHost host, string prefix, Guid orderId, Func<StreamId, TPm> factory, CancellationToken ct)
        where TPm : ProcessManager
    {
        using var scope = host.Services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IProcessManagerRepository<TPm>>();
        return await repo.LoadAsync(StreamId.ForProcessManager(prefix, WellKnownTenants.Default, orderId), factory, ct);
    }

    private static async Task<TPm?> PollAsync<TPm>(
        Func<Task<TPm?>> load, Func<TPm, bool> done, CancellationToken ct)
        where TPm : class
    {
        var deadline = DateTime.UtcNow + PollBudget;
        while (DateTime.UtcNow < deadline)
        {
            var pm = await load();
            if (pm is not null && done(pm)) return pm;
            await Task.Delay(PollInterval, ct);
        }
        var last = await load();
        return last is not null && done(last) ? last : null;
    }

    private static Address Address() => new("1 Main St", "Smalltown", "12345", "US");

    // correlationId is optional: the arc tests do not care what it is, and the metadata test
    // supplies one so the PM's events have a workflow identity to be asserted against.
    private static EventEnvelope Envelope(
        StreamId streamId, int version, IDomainEvent payload, DateTime now, Guid? correlationId = null)
    {
        var eventId = Guid.NewGuid();
        var metadata = new EventMetadata(
            EventId: eventId,
            CorrelationId: correlationId ?? Guid.NewGuid(),
            CausationId: Guid.NewGuid(),
            ActorId: Guid.Empty,
            Source: "integration-test",
            SchemaVersion: 1,
            OccurredUtc: now,
            Tenant: WellKnownTenants.Default);
        return new EventEnvelope(
            StreamId: streamId,
            StreamVersion: version,
            EventId: eventId,
            EventType: payload.GetType().Name,
            EventVersion: 1,
            Payload: payload,
            Metadata: metadata,
            OccurredUtc: now,
            GlobalPosition: 0);
    }
}
