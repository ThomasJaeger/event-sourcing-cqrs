using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.Domain.Billing.Events;
using EventSourcingCqrs.Domain.Fulfillment.Events;
using EventSourcingCqrs.Domain.Sales;
using EventSourcingCqrs.Domain.Sales.Events;
using EventSourcingCqrs.Domain.Sales.ReadModels;
using EventSourcingCqrs.Domain.SharedKernel;
using EventSourcingCqrs.Infrastructure.EventStore.Postgres;
using EventSourcingCqrs.Infrastructure.Outbox;
using EventSourcingCqrs.Infrastructure.Versioning;
using EventSourcingCqrs.Infrastructure.ReadModels.Postgres;
using EventSourcingCqrs.Projections.CustomerSummary;
using EventSourcingCqrs.Projections.Infrastructure;
using EventSourcingCqrs.Projections.InventoryDashboard;
using EventSourcingCqrs.Projections.OrderDetail;
using EventSourcingCqrs.Projections.OrderList;
using EventSourcingCqrs.Projections.OrderThroughput;
using EventSourcingCqrs.TestInfrastructure;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Xunit;

namespace EventSourcingCqrs.Projections.Tests;

// Commit 1 (the bundle): projection-side tenant tagging. Container-backed RED on a real
// PostgreSQL so tenant_id is an observable column. Four groups:
//
//  A  per-UoW write tagging: a projection write tags the row with the current tenant.
//  B  per-lookup isolation: a projection-private lookup does not resolve across tenants.
//  C  fail-closed at the write: a write with no tenant set throws, not stores the DEFAULT.
//  D  set-point end-to-end: live dispatch and replay supply each event's metadata tenant.
//
// Tenancy on the write path is driven by the current-tenant accessor, so the per-projection
// tests flip a StubTenantAccessor between phases. The event metadata tenant is incidental on
// the A/B/C paths (the write reads the accessor, not the event); Group D is the one that
// requires the metadata tenant to reach the accessor, which is the set-point's job.
//
// Group B populates under WellKnownTenants.Default (not a non-default tenant): until this
// commit's write-tagging lands, a handler-driven write persists under the 0017 DEFAULT
// regardless of the accessor, so populating under DEFAULT keeps the observed row readable
// under the same tenant in both the RED and the GREEN state and isolates the lookup
// predicate from the write-tagging. The query runs under a distinct tenant (TenantB).
public sealed class ProjectionTenantTaggingTests : IClassFixture<PostgresFixture>
{
    // Internal so CrossTenantProjectionCases can invoke the same harness the cross-tenant coverage cases
    // extend; the standalone groups below and the registry-driven meta-test then share one harness.
    internal static readonly TenantId TenantA =
        TenantId.From(Guid.Parse("a0000000-0000-0000-0000-0000000000a1"));
    internal static readonly TenantId TenantB =
        TenantId.From(Guid.Parse("b0000000-0000-0000-0000-0000000000b2"));
    private static readonly TenantId Default = WellKnownTenants.Default;
    internal static readonly DateTime At = new(2026, 5, 24, 12, 0, 0, DateTimeKind.Utc);

    private readonly PostgresFixture _fixture;

    public ProjectionTenantTaggingTests(PostgresFixture fixture) => _fixture = fixture;

    // ---- Group A: a projection write tags the row with the current tenant ----

    [Fact]
    public Task OrderList_write_tags_the_row_with_the_current_tenant()
        => CrossTenantProjectionCases.For(_fixture)[typeof(OrderListProjection)]();

    [Fact]
    public Task OrderDetail_write_tags_the_line_with_the_current_tenant()
        => CrossTenantProjectionCases.For(_fixture)[typeof(OrderDetailProjection)]();

    [Fact]
    public Task CustomerSummary_write_tags_the_row_with_the_current_tenant()
        => CrossTenantProjectionCases.For(_fixture)[typeof(CustomerSummaryProjection)]();

    [Fact]
    public Task InventoryDashboard_write_tags_the_row_with_the_current_tenant()
        => CrossTenantProjectionCases.For(_fixture)[typeof(InventoryDashboardProjection)]();

    // ---- Group B: a projection-private lookup does not resolve across tenants ----
    // Populate under the default tenant; query under TenantB; observe under the default tenant.

    [Fact]
    public async Task OrderList_shipment_lookup_does_not_resolve_across_tenants()
    {
        await using var ds = NpgsqlDataSource.Create(await _fixture.CreateMigratedDatabaseAsync());
        var (store, projection, stub) = NewOrderList(ds, Default);
        var orderId = Guid.NewGuid();
        var shipmentId = Guid.NewGuid();

        stub.Current = Default;
        await projection.HandleAsync(
            Ctx(new OrderPlaced(orderId, Guid.NewGuid(), new Money(10m, Currency.USD), At), 1),
            CancellationToken.None);
        await projection.HandleAsync(
            Ctx(new ShipmentScheduled(shipmentId, orderId, SampleAddress(), [], At), 2),
            CancellationToken.None);

        // A return event under another tenant must not resolve the default tenant's mapping.
        stub.Current = TenantB;
        await projection.HandleAsync(
            Ctx(new ShipmentReturned(shipmentId, "damaged", At), 3), CancellationToken.None);

        stub.Current = Default;
        var row = await store.GetAsync(orderId, CancellationToken.None);
        row.Should().NotBeNull();
        row!.IsReturned.Should().BeFalse(
            "another tenant's ShipmentReturned must not resolve this tenant's shipment-to-order mapping");
    }

    [Fact]
    public async Task OrderDetail_shipment_lookup_does_not_resolve_across_tenants()
    {
        await using var ds = NpgsqlDataSource.Create(await _fixture.CreateMigratedDatabaseAsync());
        var (store, projection, stub) = NewOrderDetail(ds, Default);
        var orderId = Guid.NewGuid();
        var shipmentId = Guid.NewGuid();

        stub.Current = Default;
        await projection.HandleAsync(
            Ctx(new ShipmentScheduled(shipmentId, orderId, SampleAddress(), [], At), 1),
            CancellationToken.None);

        stub.Current = TenantB;
        await projection.HandleAsync(
            Ctx(new ShipmentReturned(shipmentId, "damaged", At), 2), CancellationToken.None);

        // Resolution appends a ShipmentReturned timeline entry; the no-op leaves none.
        stub.Current = Default;
        var timeline = await store.GetTimelineAsync(orderId, CancellationToken.None);
        timeline.Select(t => t.EventType).Should().NotContain(
            nameof(ShipmentReturned),
            "another tenant's ShipmentReturned must not resolve this tenant's shipment-to-order mapping");
    }

    [Fact]
    public async Task OrderDetail_payment_lookup_does_not_resolve_across_tenants()
    {
        await using var ds = NpgsqlDataSource.Create(await _fixture.CreateMigratedDatabaseAsync());
        var (store, projection, stub) = NewOrderDetail(ds, Default);
        var orderId = Guid.NewGuid();
        var paymentId = Guid.NewGuid();

        stub.Current = Default;
        await projection.HandleAsync(
            Ctx(new PaymentAuthorized(paymentId, orderId, new Money(10m, Currency.USD), "ref-1", At), 1),
            CancellationToken.None);

        stub.Current = TenantB;
        await projection.HandleAsync(
            Ctx(new PaymentCaptured(paymentId, new Money(10m, Currency.USD), At), 2),
            CancellationToken.None);

        stub.Current = Default;
        var timeline = await store.GetTimelineAsync(orderId, CancellationToken.None);
        timeline.Select(t => t.EventType).Should().NotContain(
            nameof(PaymentCaptured),
            "another tenant's PaymentCaptured must not resolve this tenant's payment-to-order mapping");
    }

    [Fact]
    public async Task CustomerSummary_order_lookup_does_not_resolve_across_tenants()
    {
        await using var ds = NpgsqlDataSource.Create(await _fixture.CreateMigratedDatabaseAsync());
        var (store, projection, stub) = NewCustomerSummary(ds, Default);
        var orderId = Guid.NewGuid();
        var customerId = Guid.NewGuid();

        stub.Current = Default;
        await projection.HandleAsync(
            Ctx(new OrderPlaced(orderId, customerId, new Money(50m, Currency.USD), At), 1),
            CancellationToken.None);

        stub.Current = TenantB;
        await projection.HandleAsync(
            Ctx(new OrderCancelled(orderId, "changed mind", Guid.NewGuid(), At), 2),
            CancellationToken.None);

        stub.Current = Default;
        var summary = await store.GetAsync(customerId, CancellationToken.None);
        summary.Should().NotBeNull();
        summary!.OrderCount.Should().Be(
            1, "another tenant's OrderCancelled must not resolve this tenant's per-order lookup and net it out");
    }

    [Fact]
    public async Task InventoryDashboard_reservation_lookup_does_not_resolve_across_tenants()
    {
        await using var ds = NpgsqlDataSource.Create(await _fixture.CreateMigratedDatabaseAsync());
        var (store, projection, stub) = NewInventoryDashboard(ds, Default);
        var inventoryId = Guid.NewGuid();
        var orderId = Guid.NewGuid();
        var lineId = Guid.NewGuid();

        stub.Current = Default;
        await projection.HandleAsync(
            Ctx(new InventoryCreated(inventoryId, "SKU-RES", At), 1), CancellationToken.None);
        await projection.HandleAsync(
            Ctx(new InventoryReserved(inventoryId, orderId, lineId, "SKU-RES", 5, At), 2),
            CancellationToken.None);

        stub.Current = TenantB;
        await projection.HandleAsync(
            Ctx(new InventoryReleased(inventoryId, orderId, lineId, "cancelled", At), 3),
            CancellationToken.None);

        stub.Current = Default;
        var dash = await store.GetBySkuAsync("SKU-RES", CancellationToken.None);
        dash.Should().NotBeNull();
        dash!.ReservedQuantity.Should().Be(
            5, "another tenant's InventoryReleased must not resolve this tenant's reservation and decrement it");
    }

    // ---- Group C: a write with no tenant set fails closed ----
    // One representative UoW: the fail-closed contract is shared via ReadModelTenant.ResolveOrThrow.

    [Fact]
    public async Task OrderList_write_with_no_tenant_set_fails_closed_via_ResolveOrThrow()
    {
        await using var ds = NpgsqlDataSource.Create(await _fixture.CreateMigratedDatabaseAsync());
        var (store, _, _) = NewOrderList(ds, tenant: null);
        var row = new OrderListRow(
            Guid.NewGuid(), Guid.NewGuid(), OrderStatus.Placed, new Money(10m, Currency.USD),
            At, At, false, null);

        Func<Task> write = async () =>
        {
            await using var uow = await store.BeginAsync(CancellationToken.None);
            await uow.InsertAsync(row, CancellationToken.None);
            await uow.CommitAsync("order-list", 1, CancellationToken.None);
        };

        await write.Should().ThrowAsync<MissingTenantContextException>();
    }

    // ---- Group D: the set-point supplies each event's metadata tenant to the write ----
    // No manual accessor is set; the dispatcher / replayer must read the tenant from metadata.

    [Fact]
    public async Task SetPoint_on_live_dispatch_tags_each_row_with_its_events_tenant()
    {
        var connStr = await _fixture.CreateMigratedDatabaseAsync();
        await using var ds = NpgsqlDataSource.Create(connStr);
        var arr = await ArrangeSetPointAsync(ds);

        var dispatcher = BuildDispatcher(arr.Projection, arr.Stub);
        foreach (var envelope in await ReadAllAsync(arr.EventStore))
        {
            await dispatcher.DispatchAsync(ToOutboxMessage(envelope), CancellationToken.None);
        }

        await AssertPerTenantVisibilityAsync(arr.Store, arr.Stub, arr.OrderA, arr.OrderB);
    }

    [Fact]
    public async Task SetPoint_on_replay_tags_each_row_with_its_events_tenant()
    {
        var connStr = await _fixture.CreateMigratedDatabaseAsync();
        await using var ds = NpgsqlDataSource.Create(connStr);
        var arr = await ArrangeSetPointAsync(ds);

        await new ProjectionReplayer(arr.EventStore, arr.Projection, arr.Stub)
            .ReplayAsync(0, CancellationToken.None);

        await AssertPerTenantVisibilityAsync(arr.Store, arr.Stub, arr.OrderA, arr.OrderB);
    }

    private static async Task AssertPerTenantVisibilityAsync(
        PostgresOrderListStore store, StubTenantAccessor stub, Guid orderA, Guid orderB)
    {
        stub.Current = TenantA;
        (await store.GetAsync(orderA, CancellationToken.None))
            .Should().NotBeNull("order A's event carried tenant A, so the set-point must tag its row tenant A");
        (await store.GetAsync(orderB, CancellationToken.None)).Should().BeNull();

        stub.Current = TenantB;
        (await store.GetAsync(orderB, CancellationToken.None))
            .Should().NotBeNull("order B's event carried tenant B, so the set-point must tag its row tenant B");
        (await store.GetAsync(orderA, CancellationToken.None)).Should().BeNull();
    }

    private static async Task<SetPointArrangement> ArrangeSetPointAsync(NpgsqlDataSource ds)
    {
        var eventStore = new PostgresEventStore(
            new NpgsqlConnectionFactory(ds),
            new EventTypeRegistry().Register<OrderPlaced>(),
            new ProcessManagerEventTypeRegistry(),
            EventStoreJsonOptions.Create(),
            new EventUpcasterPipeline(new EventTypeRegistry().Register<OrderPlaced>(), []));
        var readModelFactory = new NpgsqlReadModelConnectionFactory(ds);
        // No manual tenant: the set-point under test must supply it from each event's metadata.
        var stub = new StubTenantAccessor { Current = null };
        var store = new PostgresOrderListStore(
            readModelFactory, new PostgresCheckpointStore(readModelFactory),
            TestNotificationPublisher.Create(), stub);
        var projection = new OrderListProjection(store, NullLogger<OrderListProjection>.Instance);

        var orderA = Guid.NewGuid();
        var orderB = Guid.NewGuid();
        var streamA = StreamId.ForAggregate<Order>(TenantA, orderA);
        var streamB = StreamId.ForAggregate<Order>(TenantB, orderB);

        await eventStore.AppendAsync(streamA, 0,
            [Env(streamA, 1, new OrderPlaced(orderA, Guid.NewGuid(), new Money(20m, Currency.USD), At), TenantA)],
            CancellationToken.None);
        await eventStore.AppendAsync(streamB, 0,
            [Env(streamB, 1, new OrderPlaced(orderB, Guid.NewGuid(), new Money(30m, Currency.USD), At), TenantB)],
            CancellationToken.None);

        return new SetPointArrangement(eventStore, store, projection, stub, orderA, orderB);
    }

    // ---- builders and helpers ----

    internal static (PostgresOrderListStore Store, OrderListProjection Projection, StubTenantAccessor Stub)
        NewOrderList(NpgsqlDataSource ds, TenantId? tenant)
    {
        var factory = new NpgsqlReadModelConnectionFactory(ds);
        var stub = new StubTenantAccessor { Current = tenant };
        var store = new PostgresOrderListStore(
            factory, new PostgresCheckpointStore(factory), TestNotificationPublisher.Create(), stub);
        return (store, new OrderListProjection(store, NullLogger<OrderListProjection>.Instance), stub);
    }

    internal static (PostgresOrderDetailStore Store, OrderDetailProjection Projection, StubTenantAccessor Stub)
        NewOrderDetail(NpgsqlDataSource ds, TenantId? tenant)
    {
        var factory = new NpgsqlReadModelConnectionFactory(ds);
        var stub = new StubTenantAccessor { Current = tenant };
        var store = new PostgresOrderDetailStore(
            factory, new PostgresCheckpointStore(factory), TestNotificationPublisher.Create(), stub);
        return (
            store,
            new OrderDetailProjection(store, EventStoreJsonOptions.Create(), NullLogger<OrderDetailProjection>.Instance),
            stub);
    }

    internal static (PostgresCustomerSummaryStore Store, CustomerSummaryProjection Projection, StubTenantAccessor Stub)
        NewCustomerSummary(NpgsqlDataSource ds, TenantId? tenant)
    {
        var factory = new NpgsqlReadModelConnectionFactory(ds);
        var stub = new StubTenantAccessor { Current = tenant };
        var store = new PostgresCustomerSummaryStore(
            factory, new PostgresCheckpointStore(factory), TestNotificationPublisher.Create(), stub);
        return (store, new CustomerSummaryProjection(store, NullLogger<CustomerSummaryProjection>.Instance), stub);
    }

    internal static (PostgresInventoryDashboardStore Store, InventoryDashboardProjection Projection, StubTenantAccessor Stub)
        NewInventoryDashboard(NpgsqlDataSource ds, TenantId? tenant)
    {
        var factory = new NpgsqlReadModelConnectionFactory(ds);
        var stub = new StubTenantAccessor { Current = tenant };
        var store = new PostgresInventoryDashboardStore(
            factory, new PostgresCheckpointStore(factory), TestNotificationPublisher.Create(), stub);
        return (
            store,
            new InventoryDashboardProjection(store, NullLogger<InventoryDashboardProjection>.Instance),
            stub);
    }

    internal static (PostgresOrderThroughputStore Store, OrderThroughputProjection Projection, StubTenantAccessor Stub)
        NewOrderThroughput(NpgsqlDataSource ds, TenantId? tenant)
    {
        var factory = new NpgsqlReadModelConnectionFactory(ds);
        var stub = new StubTenantAccessor { Current = tenant };
        var store = new PostgresOrderThroughputStore(
            factory, new PostgresCheckpointStore(factory), TestNotificationPublisher.Create(), stub);
        return (store, new OrderThroughputProjection(store), stub);
    }

    internal static EventContext<TEvent> Ctx<TEvent>(TEvent @event, long position, TenantId? tenant = null)
        where TEvent : IDomainEvent
        => new(
            @event,
            new EventMetadata(
                EventId: Guid.NewGuid(),
                CorrelationId: Guid.NewGuid(),
                CausationId: Guid.NewGuid(),
                ActorId: Guid.Empty,
                Source: "test",
                SchemaVersion: 1,
                OccurredUtc: At,
                Tenant: tenant ?? Default),
            position);

    private static Address SampleAddress() => new("1 Main St", "Smalltown", "12345", "US");


    private static EventEnvelope Env(StreamId streamId, int version, IDomainEvent payload, TenantId tenant)
    {
        var eventId = Guid.NewGuid();
        var metadata = new EventMetadata(
            EventId: eventId,
            CorrelationId: Guid.NewGuid(),
            CausationId: Guid.NewGuid(),
            ActorId: Guid.Empty,
            Source: "test",
            SchemaVersion: 1,
            OccurredUtc: At,
            Tenant: tenant);
        return new EventEnvelope(
            StreamId: streamId,
            StreamVersion: version,
            EventId: eventId,
            EventType: payload.GetType().Name,
            EventVersion: 1,
            Payload: payload,
            Metadata: metadata,
            OccurredUtc: At,
            GlobalPosition: 0);
    }

    private static async Task<List<EventEnvelope>> ReadAllAsync(IEventStore eventStore)
    {
        var envelopes = new List<EventEnvelope>();
        await foreach (var envelope in eventStore.ReadAllAsync(0, CancellationToken.None))
        {
            envelopes.Add(envelope);
        }
        return envelopes;
    }

    private static OutboxMessage ToOutboxMessage(EventEnvelope envelope)
        => new(
            OutboxId: envelope.GlobalPosition,
            EventId: envelope.EventId,
            EventType: envelope.EventType,
            EventVersion: envelope.EventVersion,
            Event: envelope.Payload,
            Metadata: envelope.Metadata,
            GlobalPosition: envelope.GlobalPosition,
            AttemptCount: 0);

    private static InProcessMessageDispatcher BuildDispatcher(
        OrderListProjection projection, StubTenantAccessor stub)
    {
        var services = new ServiceCollection();
        // Registered so the dispatch-path set-point can resolve the same accessor the store reads.
        services.AddSingleton<ICurrentTenantAccessor>(stub);
        services.AddSingleton<IEventHandler<OrderPlaced>>(projection);
        return new InProcessMessageDispatcher(services.BuildServiceProvider());
    }

    private sealed record SetPointArrangement(
        PostgresEventStore EventStore,
        PostgresOrderListStore Store,
        OrderListProjection Projection,
        StubTenantAccessor Stub,
        Guid OrderA,
        Guid OrderB);
}
