using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.Domain.Sales;
using EventSourcingCqrs.Domain.Sales.Events;
using EventSourcingCqrs.Domain.SharedKernel;
using EventSourcingCqrs.Infrastructure.EventStore.Postgres;
using EventSourcingCqrs.Infrastructure.Outbox;
using EventSourcingCqrs.Infrastructure.Versioning;
using EventSourcingCqrs.Infrastructure.ReadModels.Postgres;
using EventSourcingCqrs.Projections.Infrastructure;
using EventSourcingCqrs.Projections.OrderList;
using EventSourcingCqrs.Projections.OrderThroughput;
using EventSourcingCqrs.TestInfrastructure;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Xunit;

namespace EventSourcingCqrs.Projections.Tests;

// Per-tenant rebuild (P10.9): rebuilding one tenant's read model by replaying only that
// tenant's events must leave every other tenant's rows and the global catch-up checkpoint
// untouched. The harness conforms to OrderListRebuildTests: Testcontainers Postgres, events
// appended through PostgresEventStore and driven live through the in-process dispatcher, the
// read model read back per tenant. The store and the dispatcher share one tenant accessor so
// a dispatched event tags its own tenant's row. The rebuild runs the projection over a
// rebuild-mode checkpoint store (no skip, no advance) and bounds the replay at the captured
// global checkpoint, so it re-populates the tenant's rows without moving the shared checkpoint.
public class PerTenantRebuildTests : IClassFixture<PostgresFixture>
{
    private static readonly TenantId OtherTenant =
        TenantId.From(Guid.Parse("55555555-5555-5555-5555-555555555555"));
    private static readonly DateTime BaseTime = new(2026, 5, 14, 12, 0, 0, DateTimeKind.Utc);

    private readonly PostgresFixture _fixture;

    public PerTenantRebuildTests(PostgresFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task Rebuilding_one_tenant_leaves_other_tenants_untouched()
    {
        var connStr = await _fixture.CreateMigratedDatabaseAsync();
        await using var dataSource = NpgsqlDataSource.Create(connStr);
        var ctx = await ArrangeAsync(dataSource);

        // Capture the live state before the rebuild, each row read under the tenant it
        // belongs to, plus the global checkpoint the projection shares across tenants.
        ctx.TenantAccessor.Current = OtherTenant;
        var otherBefore = await ctx.OrderListStore.GetAsync(ctx.OrderC, CancellationToken.None);
        ctx.TenantAccessor.Current = WellKnownTenants.Default;
        var defaultABefore = await ctx.OrderListStore.GetAsync(ctx.OrderA, CancellationToken.None);
        var defaultBBefore = await ctx.OrderListStore.GetAsync(ctx.OrderB, CancellationToken.None);
        var checkpointBefore = await ctx.CheckpointStore.GetPositionAsync(
            ctx.ProjectionName, CancellationToken.None);

        // The live pass populated all three rows, so the equality assertions below are not
        // vacuously true against empty read models.
        otherBefore.Should().NotBeNull();
        defaultABefore.Should().NotBeNull();
        defaultBBefore.Should().NotBeNull();

        var rebuilder = new PerTenantProjectionRebuilder(
            ctx.EventStore,
            new PostgresProjectionRebuildCoordinator(new NpgsqlReadModelConnectionFactory(dataSource), ctx.CheckpointStore),
            ctx.TenantAccessor);
        await rebuilder.RebuildAsync(
            ctx.ProjectionFactory, (ITenantResettable)ctx.OrderListStore, WellKnownTenants.Default,
            CancellationToken.None);

        // The other tenant is untouched; the default tenant rebuilds to the same live values;
        // the global checkpoint is unchanged. The checkpoint-unchanged assertion pins that a
        // per-tenant rebuild does not disturb global catch-up.
        ctx.TenantAccessor.Current = OtherTenant;
        (await ctx.OrderListStore.GetAsync(ctx.OrderC, CancellationToken.None)).Should().Be(otherBefore);
        ctx.TenantAccessor.Current = WellKnownTenants.Default;
        (await ctx.OrderListStore.GetAsync(ctx.OrderA, CancellationToken.None)).Should().Be(defaultABefore);
        (await ctx.OrderListStore.GetAsync(ctx.OrderB, CancellationToken.None)).Should().Be(defaultBBefore);
        (await ctx.CheckpointStore.GetPositionAsync(ctx.ProjectionName, CancellationToken.None))
            .Should().Be(checkpointBefore);
    }

    // Characterization (ADR 0041): the per-tenant throughput rebuild already holds end to end, so this
    // pins it rather than driving new code. The generic PerTenantProjectionRebuilder (checkpoint-neutral
    // RebuildModeCheckpointStore, bounded ReplayForTenantAsync) pre-existed, and the prior commit made the
    // throughput store fit it by implementing ITenantResettable; the throughput projection drops into the
    // Func<ICheckpointStore, IProjection> factory the same way OrderList does. Rebuilding one tenant's
    // buckets restores that tenant's buckets, leaves the other tenant's untouched, and moves no global
    // checkpoint (the safety property the ADR rests on).
    [Fact]
    public async Task Rebuilding_throughput_for_one_tenant_restores_its_buckets_and_moves_no_global_checkpoint()
    {
        var connStr = await _fixture.CreateMigratedDatabaseAsync();
        await using var dataSource = NpgsqlDataSource.Create(connStr);
        var ctx = await ArrangeThroughputAsync(dataSource);

        // Capture each tenant's buckets and the shared global checkpoint before the rebuild.
        ctx.TenantAccessor.Current = OtherTenant;
        var otherBefore = await ctx.Store.GetBucketsAsync(CancellationToken.None);
        ctx.TenantAccessor.Current = WellKnownTenants.Default;
        var defaultBefore = await ctx.Store.GetBucketsAsync(CancellationToken.None);
        var checkpointBefore = await ctx.CheckpointStore.GetPositionAsync(
            ctx.ProjectionName, CancellationToken.None);

        // The live pass populated both tenants' buckets, so the equality assertions are not vacuous.
        defaultBefore.Should().NotBeEmpty();
        otherBefore.Should().NotBeEmpty();

        var rebuilder = new PerTenantProjectionRebuilder(
            ctx.EventStore,
            new PostgresProjectionRebuildCoordinator(new NpgsqlReadModelConnectionFactory(dataSource), ctx.CheckpointStore),
            ctx.TenantAccessor);
        await rebuilder.RebuildAsync(
            ctx.ProjectionFactory, (ITenantResettable)ctx.Store, WellKnownTenants.Default,
            CancellationToken.None);

        // The default tenant's buckets rebuild to the same values; the other tenant's are untouched;
        // the global checkpoint is unmoved. The checkpoint-unchanged assertion is the safety property
        // the ADR rests on.
        ctx.TenantAccessor.Current = WellKnownTenants.Default;
        (await ctx.Store.GetBucketsAsync(CancellationToken.None)).Should().BeEquivalentTo(defaultBefore);
        ctx.TenantAccessor.Current = OtherTenant;
        (await ctx.Store.GetBucketsAsync(CancellationToken.None)).Should().BeEquivalentTo(otherBefore);
        (await ctx.CheckpointStore.GetPositionAsync(ctx.ProjectionName, CancellationToken.None))
            .Should().Be(checkpointBefore);
    }

    [Fact]
    public async Task A_live_writer_waits_until_the_tenant_reset_and_replay_finish()
    {
        var connStr = await _fixture.CreateMigratedDatabaseAsync();
        await using var dataSource = NpgsqlDataSource.Create(connStr);
        var ctx = await ArrangeThroughputAsync(dataSource);
        var before = await ctx.Store.GetBucketsAsync(CancellationToken.None);
        var stream = StreamId.ForAggregate<Order>(WellKnownTenants.Default, Guid.NewGuid());
        await ctx.EventStore.AppendAsync(stream, 0,
            [Env(stream, 1, new OrderDrafted(Guid.NewGuid(), Guid.NewGuid(), BaseTime, "web"),
                WellKnownTenants.Default)], CancellationToken.None);
        var envelope = (await ctx.EventStore.ReadStreamAsync(stream)).Single();

        // Gate the existing reset port, not the production path. The live writer uses a
        // separate connection and real PostgreSQL locks, as another host would.
        var reset = new PausedTenantReset((ITenantResettable)ctx.Store);
        var rebuilder = new PerTenantProjectionRebuilder(
            ctx.EventStore,
            new PostgresProjectionRebuildCoordinator(new NpgsqlReadModelConnectionFactory(dataSource), ctx.CheckpointStore),
            ctx.TenantAccessor);
        var rebuilding = rebuilder.RebuildAsync(ctx.ProjectionFactory, reset, WellKnownTenants.Default);
        await reset.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var liveProjection = new OrderThroughputProjection(ctx.Store);
        var context = new EventContext<OrderDrafted>(
            (OrderDrafted)envelope.Payload, envelope.Metadata, envelope.GlobalPosition);
        try
        {
            using var blockedWrite = new CancellationTokenSource(TimeSpan.FromSeconds(1));
            Func<Task> write = () => liveProjection.HandleAsync(context, blockedWrite.Token);
            await write.Should().ThrowAsync<OperationCanceledException>(
                "a live projection must not commit between the rebuild's ceiling and reset");
        }
        finally
        {
            reset.Continue.TrySetResult();
            await rebuilding;
        }

        // Once replay finishes, the same event succeeds and both old and new counts survive.
        await liveProjection.HandleAsync(context, CancellationToken.None);
        var after = await ctx.Store.GetBucketsAsync(CancellationToken.None);
        after.Sum(row => row.Count).Should().Be(before.Sum(row => row.Count) + 1);
        (await ctx.CheckpointStore.GetPositionAsync(ctx.ProjectionName, CancellationToken.None))
            .Should().Be(envelope.GlobalPosition);
    }

    [Fact]
    public async Task Losing_the_checkpoint_lock_fails_the_rebuild_instead_of_reporting_success()
    {
        var connStr = await _fixture.CreateMigratedDatabaseAsync();
        await using var dataSource = NpgsqlDataSource.Create(connStr);
        var ctx = await ArrangeThroughputAsync(dataSource);
        const string leaseApplication = "projection-rebuild-loss-test";
        await using var leaseSource = NpgsqlDataSource.Create(
            new NpgsqlConnectionStringBuilder(connStr) { ApplicationName = leaseApplication }.ConnectionString);
        var rebuilder = new PerTenantProjectionRebuilder(
            ctx.EventStore,
            new PostgresProjectionRebuildCoordinator(new NpgsqlReadModelConnectionFactory(leaseSource), ctx.CheckpointStore),
            ctx.TenantAccessor);
        var reset = new PausedTenantReset((ITenantResettable)ctx.Store);
        var rebuilding = rebuilder.RebuildAsync(ctx.ProjectionFactory, reset, WellKnownTenants.Default);
        await reset.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        try
        {
            await using var terminate = dataSource.CreateCommand(
                "SELECT pg_terminate_backend(pid) FROM pg_stat_activity " +
                "WHERE datname = current_database() AND application_name = @application");
            terminate.Parameters.AddWithValue("application", leaseApplication);
            (await terminate.ExecuteScalarAsync()).Should().Be(true);
        }
        finally
        {
            reset.Continue.TrySetResult();
        }

        Func<Task> rebuild = () => rebuilding;
        await rebuild.Should().ThrowAsync<ProjectionRebuildLeaseLostException>();
    }

    [Fact]
    public async Task A_failed_rebuild_releases_the_live_writer_lock()
        => await AssertInterruptedRebuildReleasesLockAsync(cancel: false);

    [Fact]
    public async Task A_cancelled_rebuild_releases_the_live_writer_lock()
        => await AssertInterruptedRebuildReleasesLockAsync(cancel: true);

    private async Task AssertInterruptedRebuildReleasesLockAsync(bool cancel)
    {
        var connStr = await _fixture.CreateMigratedDatabaseAsync();
        await using var dataSource = NpgsqlDataSource.Create(connStr);
        var ctx = await ArrangeThroughputAsync(dataSource);
        var checkpoint = await ctx.CheckpointStore.GetPositionAsync(ctx.ProjectionName, CancellationToken.None);
        var rebuilder = new PerTenantProjectionRebuilder(
            ctx.EventStore,
            new PostgresProjectionRebuildCoordinator(new NpgsqlReadModelConnectionFactory(dataSource), ctx.CheckpointStore),
            ctx.TenantAccessor);
        using var interruption = new CancellationTokenSource();
        Func<Task> rebuild = () => rebuilder.RebuildAsync(ctx.ProjectionFactory,
            new InterruptingTenantReset(cancel ? interruption : null), WellKnownTenants.Default, interruption.Token);
        if (cancel)
            await rebuild.Should().ThrowAsync<OperationCanceledException>();
        else
            await rebuild.Should().ThrowAsync<IOException>();

        // An independent live handler must be able to acquire the checkpoint after either exit.
        var envelope = Env(StreamId.ForAggregate<Order>(WellKnownTenants.Default, Guid.NewGuid()), 1,
            new OrderDrafted(Guid.NewGuid(), Guid.NewGuid(), BaseTime, "web"), WellKnownTenants.Default);
        using var writerDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        Func<Task> write = () => new OrderThroughputProjection(ctx.Store).HandleAsync(
            new EventContext<OrderDrafted>((OrderDrafted)envelope.Payload, envelope.Metadata, checkpoint + 1),
            writerDeadline.Token);
        await write.Should().NotThrowAsync("failed and cancelled rebuilds must release their database lock");
    }

    [Fact]
    public async Task Per_tenant_read_yields_only_that_tenants_events_below_the_ceiling()
    {
        var connStr = await _fixture.CreateMigratedDatabaseAsync();
        await using var dataSource = NpgsqlDataSource.Create(connStr);
        var eventStore = new PostgresEventStore(
            new NpgsqlConnectionFactory(dataSource), CreateRegistry(), CreatePmRegistry(), EventStoreJsonOptions.Create(), new EventUpcasterPipeline(CreateRegistry(), []));

        var customer = Guid.NewGuid();
        // Two default-tenant orders span global positions 1-6 (order A at 1-3, order B at
        // 4-6); the other tenant takes 7-9; a pm- stream takes 10.
        await AppendPlacedOrderAsync(eventStore, WellKnownTenants.Default, Guid.NewGuid(), customer);
        await AppendPlacedOrderAsync(eventStore, WellKnownTenants.Default, Guid.NewGuid(), customer);
        await AppendPlacedOrderAsync(eventStore, OtherTenant, Guid.NewGuid(), customer);
        var pmStream = StreamId.ForProcessManager(
            StreamPrefixes.OrderFulfillmentPm, WellKnownTenants.Default, Guid.NewGuid());
        await eventStore.AppendProcessManagerEventsAsync(
            pmStream, 0, [PmEnvelope(pmStream)], CancellationToken.None);

        // Read up to order A's last position; order B's events (positions 4-6) sit above it.
        const long ceiling = 3;
        var read = new List<EventEnvelope>();
        await foreach (var envelope in eventStore.ReadAllForTenantAsync(
            WellKnownTenants.Default, 0, ceiling, CancellationToken.None))
        {
            read.Add(envelope);
        }

        read.Should().OnlyContain(e => e.Metadata.Tenant == WellKnownTenants.Default);
        read.Should().NotContain(e => e.Metadata.Tenant == OtherTenant);
        read.Should().OnlyContain(e => !e.StreamId.Value.StartsWith("pm-", StringComparison.Ordinal));
        read.Should().OnlyContain(e => e.GlobalPosition <= ceiling);
        // Order A's three events are below the ceiling; order B's three (positions 4-6) are
        // above it and excluded, so the count pins the ceiling rather than trusting it.
        read.Should().HaveCount(3);
    }

    // Builds the stores and the live projection over one shared tenant accessor, plus a
    // factory that builds the projection over a supplied checkpoint store (the rebuild runs
    // it over the rebuild-mode store while its writes land in the same tables). Seeds two
    // placed orders under the default tenant and one under the other tenant, plus one
    // process-manager-stream event, then drives the aggregate events live into the read model.
    private static async Task<RebuildContext> ArrangeAsync(NpgsqlDataSource dataSource)
    {
        var tenantAccessor = new StubTenantAccessor { Current = WellKnownTenants.Default };
        var eventStore = new PostgresEventStore(
            new NpgsqlConnectionFactory(dataSource), CreateRegistry(), CreatePmRegistry(), EventStoreJsonOptions.Create(), new EventUpcasterPipeline(CreateRegistry(), []));
        var readModelFactory = new NpgsqlReadModelConnectionFactory(dataSource);
        var checkpointStore = new PostgresCheckpointStore(readModelFactory);
        var orderListStore = new PostgresOrderListStore(
            readModelFactory, checkpointStore, TestNotificationPublisher.Create(), tenantAccessor);
        var liveProjection = new OrderListProjection(orderListStore, NullLogger<OrderListProjection>.Instance);

        Func<ICheckpointStore, IProjection> projectionFactory = cp =>
            new OrderListProjection(
                new PostgresOrderListStore(readModelFactory, cp, TestNotificationPublisher.Create(), tenantAccessor),
                NullLogger<OrderListProjection>.Instance);

        var customer = Guid.NewGuid();
        var orderA = Guid.NewGuid();
        var orderB = Guid.NewGuid();
        var orderC = Guid.NewGuid();

        await AppendPlacedOrderAsync(eventStore, WellKnownTenants.Default, orderA, customer);
        await AppendPlacedOrderAsync(eventStore, WellKnownTenants.Default, orderB, customer);
        await AppendPlacedOrderAsync(eventStore, OtherTenant, orderC, customer);

        var pmStream = StreamId.ForProcessManager(
            StreamPrefixes.OrderFulfillmentPm, WellKnownTenants.Default, Guid.NewGuid());
        await eventStore.AppendProcessManagerEventsAsync(
            pmStream, 0, [PmEnvelope(pmStream)], CancellationToken.None);

        // Drive the aggregate events live through the dispatcher into the read model.
        // ReadAllAsync excludes the pm- stream, so the dispatcher never sees it. The
        // dispatcher sets the shared accessor from each event's tenant, so a default event
        // tags a default row and the other tenant's event tags the other's.
        var dispatcher = BuildDispatcher(liveProjection, tenantAccessor);
        await foreach (var envelope in eventStore.ReadAllAsync(0, CancellationToken.None))
        {
            await dispatcher.DispatchAsync(ToOutboxMessage(envelope), CancellationToken.None);
        }
        tenantAccessor.Current = WellKnownTenants.Default;

        return new RebuildContext(
            eventStore, checkpointStore, orderListStore, projectionFactory, liveProjection.Name,
            tenantAccessor, orderA, orderB, orderC);
    }

    private static Task AppendPlacedOrderAsync(
        PostgresEventStore eventStore, TenantId tenant, Guid orderId, Guid customer)
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

    private static ProcessManagerEventEnvelope PmEnvelope(StreamId streamId)
    {
        var eventId = Guid.NewGuid();
        var metadata = new EventMetadata(
            EventId: eventId,
            CorrelationId: Guid.NewGuid(),
            CausationId: Guid.NewGuid(),
            ActorId: Guid.Empty,
            Source: "test",
            OccurredUtc: BaseTime,
            Tenant: WellKnownTenants.Default);
        return new ProcessManagerEventEnvelope(
            StreamId: streamId,
            StreamVersion: 1,
            EventId: eventId,
            EventType: nameof(RebuildPmTestEvent),
            EventVersion: 1,
            Payload: new RebuildPmTestEvent(0),
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

    private static InProcessMessageDispatcher BuildDispatcher(
        OrderListProjection projection, ICurrentTenantAccessor tenantAccessor)
    {
        var services = new ServiceCollection();
        // OrderPlaced is the only handled event the seed emits (OrderDrafted and
        // OrderLineAdded have no handler, so the dispatcher skips them).
        services.AddSingleton<IEventHandler<OrderPlaced>>(projection);
        services.AddSingleton(tenantAccessor);
        return new InProcessMessageDispatcher(services.BuildServiceProvider());
    }

    private static EventTypeRegistry CreateRegistry()
        => new EventTypeRegistry()
            .Register<OrderDrafted>()
            .Register<OrderLineAdded>()
            .Register<OrderPlaced>();

    private static ProcessManagerEventTypeRegistry CreatePmRegistry()
        => new ProcessManagerEventTypeRegistry().Register<RebuildPmTestEvent>();


    private sealed record RebuildPmTestEvent(int Step) : IProcessManagerEvent;

    private sealed class PausedTenantReset(ITenantResettable inner) : ITenantResettable
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Continue { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task ResetTenantAsync(TenantId tenant, CancellationToken ct = default)
        {
            Entered.TrySetResult();
            await Continue.Task.WaitAsync(ct);
            await inner.ResetTenantAsync(tenant, ct);
        }
    }

    private sealed class InterruptingTenantReset(CancellationTokenSource? cancellation) : ITenantResettable
    {
        public Task ResetTenantAsync(TenantId tenant, CancellationToken ct = default)
        {
            if (cancellation is not null)
            {
                cancellation.Cancel();
                ct.ThrowIfCancellationRequested();
            }
            throw new IOException("Injected reset failure.");
        }
    }

    private sealed record RebuildContext(
        PostgresEventStore EventStore,
        PostgresCheckpointStore CheckpointStore,
        PostgresOrderListStore OrderListStore,
        Func<ICheckpointStore, IProjection> ProjectionFactory,
        string ProjectionName,
        StubTenantAccessor TenantAccessor,
        Guid OrderA,
        Guid OrderB,
        Guid OrderC);

    // The throughput analogue of ArrangeAsync: the throughput projection over a store wired to the
    // supplied checkpoint store (the same factory shape OrderList uses), seeded with one placed order
    // per tenant driven live so both tenants' buckets and the shared checkpoint advance.
    private static async Task<ThroughputRebuildContext> ArrangeThroughputAsync(NpgsqlDataSource dataSource)
    {
        var tenantAccessor = new StubTenantAccessor { Current = WellKnownTenants.Default };
        var eventStore = new PostgresEventStore(
            new NpgsqlConnectionFactory(dataSource), CreateRegistry(), CreatePmRegistry(), EventStoreJsonOptions.Create(), new EventUpcasterPipeline(CreateRegistry(), []));
        var readModelFactory = new NpgsqlReadModelConnectionFactory(dataSource);
        var checkpointStore = new PostgresCheckpointStore(readModelFactory);
        var store = new PostgresOrderThroughputStore(
            readModelFactory, checkpointStore, TestNotificationPublisher.Create(), tenantAccessor);
        var liveProjection = new OrderThroughputProjection(store);

        Func<ICheckpointStore, IProjection> projectionFactory = cp =>
            new OrderThroughputProjection(
                new PostgresOrderThroughputStore(
                    readModelFactory, cp, TestNotificationPublisher.Create(), tenantAccessor));

        var customer = Guid.NewGuid();
        await AppendPlacedOrderAsync(eventStore, WellKnownTenants.Default, Guid.NewGuid(), customer);
        await AppendPlacedOrderAsync(eventStore, OtherTenant, Guid.NewGuid(), customer);

        var dispatcher = BuildThroughputDispatcher(liveProjection, tenantAccessor);
        await foreach (var envelope in eventStore.ReadAllAsync(0, CancellationToken.None))
        {
            await dispatcher.DispatchAsync(ToOutboxMessage(envelope), CancellationToken.None);
        }
        tenantAccessor.Current = WellKnownTenants.Default;

        return new ThroughputRebuildContext(
            eventStore, checkpointStore, store, projectionFactory, liveProjection.Name, tenantAccessor);
    }

    private static InProcessMessageDispatcher BuildThroughputDispatcher(
        OrderThroughputProjection projection, ICurrentTenantAccessor tenantAccessor)
    {
        var services = new ServiceCollection();
        // The throughput projection handles every order event; the seed emits OrderDrafted,
        // OrderLineAdded, and OrderPlaced, so all three route to it.
        services.AddSingleton<IEventHandler<OrderDrafted>>(projection);
        services.AddSingleton<IEventHandler<OrderLineAdded>>(projection);
        services.AddSingleton<IEventHandler<OrderPlaced>>(projection);
        services.AddSingleton(tenantAccessor);
        return new InProcessMessageDispatcher(services.BuildServiceProvider());
    }

    private sealed record ThroughputRebuildContext(
        PostgresEventStore EventStore,
        PostgresCheckpointStore CheckpointStore,
        PostgresOrderThroughputStore Store,
        Func<ICheckpointStore, IProjection> ProjectionFactory,
        string ProjectionName,
        StubTenantAccessor TenantAccessor);
}
