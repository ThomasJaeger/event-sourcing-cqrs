using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.Domain.Fulfillment;
using EventSourcingCqrs.Domain.Fulfillment.Events;
using EventSourcingCqrs.Domain.Sales;
using EventSourcingCqrs.Domain.Sales.Events;
using EventSourcingCqrs.Domain.Sales.ReadModels;
using EventSourcingCqrs.Domain.SharedKernel;
using EventSourcingCqrs.Infrastructure.Outbox;
using EventSourcingCqrs.Infrastructure.ReadModels.Postgres;
using EventSourcingCqrs.Infrastructure.SignalR;
using EventSourcingCqrs.Projections.Infrastructure;
using EventSourcingCqrs.Projections.OrderThroughput;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EventSourcingCqrs.IntegrationTests.AdminConsole;

// Phase 12 commit 6a: the Replay Tool page drives a per-tenant throughput rebuild from the
// AdminConsole host. This composition test boots the real host (DI-validated on build, commit 6a-0)
// and resolves the whole rebuild graph from it: the rebuilder, the event store, the throughput store,
// the tenant accessor, and the notification publisher, composed host-side in commit 6a. Every component
// is resolved from the host so the test exercises the real composition, not a hand-built provider. It
// seeds a tenant's cross-context history, rebuilds one tenant, and asserts its buckets restore, the
// other tenant is untouched, and the global checkpoint is unmoved.
public class AdminConsoleThroughputRebuildCompositionTests : IClassFixture<AdminConsoleAdmitFixture>
{
    private static readonly TenantId TenantA = TenantId.From(Guid.Parse("a1a1a1a1-0000-0000-0000-000000000001"));
    private static readonly TenantId TenantB = TenantId.From(Guid.Parse("b2b2b2b2-0000-0000-0000-000000000002"));
    private static readonly DateTime BaseTime = new(2026, 5, 14, 12, 0, 0, DateTimeKind.Utc);

    private readonly AdminConsoleAdmitFixture _fixture;

    public AdminConsoleThroughputRebuildCompositionTests(AdminConsoleAdmitFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Rebuilding_throughput_for_one_tenant_from_the_admin_console_host_restores_its_buckets()
    {
        using var scope = _fixture.Factory.Services.CreateScope();
        var sp = scope.ServiceProvider;

        // The rebuild composition, resolved from the booted AdminConsole host. The host registers none
        // of these, so the rebuilder resolve throws here: the RED. The remaining resolves and the seed
        // and rebuild below are the GREEN-ready shape the page's registration will satisfy.
        var rebuilder = sp.GetRequiredService<PerTenantProjectionRebuilder>();
        var eventStore = sp.GetRequiredService<IEventStore>();
        var tenantAccessor = sp.GetRequiredService<ICurrentTenantAccessor>();
        var store = sp.GetRequiredService<IOrderThroughputStore>();
        var checkpointStore = sp.GetRequiredService<ICheckpointStore>();
        var readModelFactory = sp.GetRequiredService<IReadModelConnectionFactory>();
        var publisher = sp.GetRequiredService<PostgresPgNotifyPublisher>();

        // Tenant A: a placed order (drives throughput buckets) plus a cross-context InventoryCreated, so
        // A's replayable history spans Sales and Fulfillment and the replay must deserialize both. Tenant
        // B: a placed order only. Appended through the host's own event store.
        var customer = Guid.NewGuid();
        await AppendPlacedOrderAsync(eventStore, TenantA, Guid.NewGuid(), customer);
        await AppendInventoryCreatedAsync(eventStore, TenantA, Guid.NewGuid());
        await AppendPlacedOrderAsync(eventStore, TenantB, Guid.NewGuid(), customer);

        // Drive the throughput projection over the seed so both tenants' buckets exist and the shared
        // "order-throughput" checkpoint advances. The InventoryCreated has no handler, so it is skipped.
        var dispatcher = BuildThroughputDispatcher(new OrderThroughputProjection(store), tenantAccessor);
        await foreach (var envelope in eventStore.ReadAllAsync(0, CancellationToken.None))
        {
            await dispatcher.DispatchAsync(ToOutboxMessage(envelope), CancellationToken.None);
        }

        tenantAccessor.Current = TenantA;
        var aBefore = await store.GetBucketsAsync(CancellationToken.None);
        tenantAccessor.Current = TenantB;
        var bBefore = await store.GetBucketsAsync(CancellationToken.None);
        var checkpointBefore = await checkpointStore.GetPositionAsync(
            ProjectionNames.OrderThroughput, CancellationToken.None);
        aBefore.Should().NotBeEmpty();
        bBefore.Should().NotBeEmpty();

        // Rebuild only tenant A through the host-composed rebuilder: a factory that builds the throughput
        // projection over the supplied (rebuild-mode) checkpoint store, the throughput store as the reset.
        Func<ICheckpointStore, IProjection> projectionFactory = cp =>
            new OrderThroughputProjection(
                new PostgresOrderThroughputStore(readModelFactory, cp, publisher, tenantAccessor));
        await rebuilder.RebuildAsync(
            projectionFactory, (ITenantResettable)store, TenantA, CancellationToken.None);

        // Tenant A's buckets are restored, tenant B's are untouched, and the global checkpoint is unmoved.
        tenantAccessor.Current = TenantA;
        (await store.GetBucketsAsync(CancellationToken.None)).Should().BeEquivalentTo(aBefore);
        tenantAccessor.Current = TenantB;
        (await store.GetBucketsAsync(CancellationToken.None)).Should().BeEquivalentTo(bBefore);
        (await checkpointStore.GetPositionAsync(ProjectionNames.OrderThroughput, CancellationToken.None))
            .Should().Be(checkpointBefore);
    }

    private static Task AppendPlacedOrderAsync(
        IEventStore eventStore, TenantId tenant, Guid orderId, Guid customer)
    {
        var stream = StreamId.ForAggregate<Order>(tenant, orderId);
        return eventStore.AppendAsync(stream, 0,
        [
            Env(stream, 1, new OrderDrafted(orderId, customer, BaseTime, "web"), tenant),
            Env(stream, 2, new OrderLineAdded(
                orderId, Guid.NewGuid(), "SKU-1", 1, new Money(20m, Currency.USD), BaseTime), tenant),
            Env(stream, 3, new OrderPlaced(
                orderId, customer, new Money(20m, Currency.USD), BaseTime.AddHours(1)), tenant),
        ], CancellationToken.None);
    }

    private static Task AppendInventoryCreatedAsync(
        IEventStore eventStore, TenantId tenant, Guid inventoryId)
    {
        var stream = StreamId.ForAggregate<Inventory>(tenant, inventoryId);
        return eventStore.AppendAsync(stream, 0,
            [Env(stream, 1, new InventoryCreated(inventoryId, "SKU-INV-" + inventoryId.ToString("N"), BaseTime), tenant)],
            CancellationToken.None);
    }

    private static EventEnvelope Env(StreamId streamId, int version, IDomainEvent payload, TenantId tenant)
    {
        var eventId = Guid.NewGuid();
        var metadata = new EventMetadata(
            EventId: eventId,
            CorrelationId: Guid.NewGuid(),
            CausationId: Guid.NewGuid(),
            ActorId: Guid.Empty,
            Source: "test",
            OccurredUtc: BaseTime,
            Tenant: tenant);
        return new EventEnvelope(
            StreamId: streamId,
            StreamVersion: version,
            EventId: eventId,
            EventType: payload.GetType().Name,
            EventVersion: 1,
            Payload: payload,
            Metadata: metadata,
            OccurredUtc: BaseTime,
            GlobalPosition: 0);
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

    private static InProcessMessageDispatcher BuildThroughputDispatcher(
        OrderThroughputProjection projection, ICurrentTenantAccessor tenantAccessor)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IEventHandler<OrderDrafted>>(projection);
        services.AddSingleton<IEventHandler<OrderLineAdded>>(projection);
        services.AddSingleton<IEventHandler<OrderPlaced>>(projection);
        services.AddSingleton(tenantAccessor);
        return new InProcessMessageDispatcher(services.BuildServiceProvider());
    }
}
