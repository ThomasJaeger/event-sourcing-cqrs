using System.Data.Common;
using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.Domain.Access.Events;
using EventSourcingCqrs.Domain.Sales.Events;
using EventSourcingCqrs.Infrastructure.EventStore.Postgres;
using EventSourcingCqrs.Infrastructure.ReadModels.Postgres;
using EventSourcingCqrs.Infrastructure.Versioning;
using EventSourcingCqrs.Projections.Infrastructure;
using EventSourcingCqrs.TestInfrastructure;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Xunit;

namespace EventSourcingCqrs.Projections.Tests;

// The catch-up waiter: given the event types a caller has just written, wait until every
// projection that subscribes to one of them has checkpointed at or past the head of the log.
// ProjectionCatchUpWaiter does not exist yet; these facts drive it into being.
//
// Why it needs a service provider. A projection advances only on events it handles, and the
// container is the only place the event-to-projection map is readable: GetServices over the
// closed IEventHandler<TEvent> answers it, while nothing answers the reverse direction. Waiting
// for every registered projection instead would hang forever on a projection that subscribes to
// none of the written events, which is the mistake these facts exist to forbid.
//
// Why the expected sets are derived from the container rather than written as literals. A
// subscription set that drifts must not quietly turn a real fact into a wrong one; the fact
// asserts the waiter agrees with the registration, not that either matches a list in this file.
//
// Time. Projections.Tests carries no FakeTimeProvider package, unlike Infrastructure.Tests, so
// the bound-exhaustion fact hand-rolls a TimeProvider the way SqlServerDelayQueueProcessorTests
// hand-rolls FixedTimeProvider. It advances GetUtcNow on each read so the deadline expires after
// a few polls, and the poll interval stays at a millisecond so no wall clock is spent waiting.
public class ProjectionCatchUpWaiterTests : IClassFixture<PostgresFixture>
{
    private static readonly DateTime BaseTime = new(2026, 8, 22, 12, 0, 0, DateTimeKind.Utc);
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(1);

    private readonly PostgresFixture _fixture;

    public ProjectionCatchUpWaiterTests(PostgresFixture fixture)
    {
        _fixture = fixture;
    }

    // Fact 1. Every projection subscribing to the written event is at the head, so the wait
    // returns, and it reports exactly those projections and no others.
    [Fact]
    public async Task Waiting_on_one_event_type_reports_exactly_the_projections_that_handle_it()
    {
        var connectionString = await _fixture.CreateMigratedDatabaseAsync();
        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        await AppendProbeEventsAsync(dataSource);
        var head = await HeadAsync(dataSource);

        await using var provider = ComposeReadModels(connectionString);
        var handling = NamesHandling<OrderPlaced>(provider);
        // A derivation that came back empty would let every assertion below pass vacuously.
        handling.Should().NotBeEmpty();
        foreach (var name in handling)
        {
            await AdvanceAndCommitAsync(dataSource, name, head);
        }

        var waiter = CreateWaiter(dataSource, provider, TimeProvider.System);
        var waited = await waiter.WaitForCatchUpAsync(
            [typeof(OrderPlaced)], Budget, PollInterval, CancellationToken.None);

        waited.Should().BeEquivalentTo(handling);
    }

    // Fact 2. No projection subscribes to the written event, so there is nothing to wait for.
    // Both ports throw on any call, so the fact fails if the waiter reads either one.
    [Fact]
    public async Task Waiting_on_an_event_no_projection_handles_returns_empty_without_reading_a_checkpoint()
    {
        await using var provider = ComposeProbeProjectionOnly();
        var waiter = new ProjectionCatchUpWaiter(
            new ThrowingHeadPosition(),
            new ThrowingCheckpointStore(),
            provider,
            TimeProvider.System);

        var waited = await waiter.WaitForCatchUpAsync(
            [typeof(UnhandledProbe)], Budget, PollInterval, CancellationToken.None);

        waited.Should().BeEmpty();
    }

    // Fact 3. The reported set follows the event types asked for. Adding RoleAssigned to the
    // request adds the roles projection; asking for OrderPlaced alone leaves it out. The fact is
    // the difference between the two calls, so a waiter that always reported every registered
    // projection would fail the second assertion.
    [Fact]
    public async Task The_reported_set_widens_and_narrows_with_the_event_types_asked_for()
    {
        var connectionString = await _fixture.CreateMigratedDatabaseAsync();
        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        await AppendProbeEventsAsync(dataSource);
        var head = await HeadAsync(dataSource);

        await using var provider = ComposeReadModels(connectionString);
        var handlingOrderPlaced = NamesHandling<OrderPlaced>(provider);
        var handlingRoleAssigned = NamesHandling<RoleAssigned>(provider);
        handlingOrderPlaced.Should().NotBeEmpty();
        handlingRoleAssigned.Should().Contain(ProjectionNames.CurrentRoles);
        handlingOrderPlaced.Should().NotContain(ProjectionNames.CurrentRoles);

        foreach (var name in handlingOrderPlaced.Concat(handlingRoleAssigned).Distinct())
        {
            await AdvanceAndCommitAsync(dataSource, name, head);
        }

        var waiter = CreateWaiter(dataSource, provider, TimeProvider.System);

        var both = await waiter.WaitForCatchUpAsync(
            [typeof(OrderPlaced), typeof(RoleAssigned)], Budget, PollInterval, CancellationToken.None);
        var orderOnly = await waiter.WaitForCatchUpAsync(
            [typeof(OrderPlaced)], Budget, PollInterval, CancellationToken.None);

        both.Should().BeEquivalentTo(handlingOrderPlaced.Concat(handlingRoleAssigned).Distinct());
        both.Should().Contain(ProjectionNames.CurrentRoles);
        orderOnly.Should().NotContain(ProjectionNames.CurrentRoles);
    }

    // Fact 4. The checkpoint never reaches the head, so the bound decides the outcome. No
    // database and no sleep: the ports are hand-written and the deadline is driven by a
    // TimeProvider that advances on each read.
    [Fact]
    public async Task Bound_exhaustion_throws_naming_the_lagging_projection_and_its_position()
    {
        await using var provider = ComposeProbeProjectionOnly();
        var waiter = new ProjectionCatchUpWaiter(
            new FixedHeadPosition(9),
            new StuckCheckpointStore(7),
            provider,
            new AdvancingTimeProvider(new DateTimeOffset(BaseTime), TimeSpan.FromSeconds(4)));

        var act = async () => await waiter.WaitForCatchUpAsync(
            [typeof(WaiterProbe)], Budget, PollInterval, CancellationToken.None);

        var thrown = await act.Should().ThrowAsync<TimeoutException>();
        thrown.Which.Message.Should().Contain(ProbeProjection.ProjectionName).And.Contain("7");
    }

    // Arrangement shared by the facts above.

    private static ProjectionCatchUpWaiter CreateWaiter(
        NpgsqlDataSource dataSource, IServiceProvider provider, TimeProvider timeProvider)
        => new(
            new PostgresEventStoreHeadReader(new NpgsqlConnectionFactory(dataSource)),
            provider.GetRequiredService<ICheckpointStore>(),
            provider,
            timeProvider);

    // The production read-model surface against a real database, the composition
    // ProjectionRosterCoverageTests and the cross-tenant projection coverage harness both use.
    private static ServiceProvider ComposeReadModels(string connectionString)
    {
        var services = new ServiceCollection();
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddPostgresEventStore(opts => opts.ConnectionString = connectionString);
        services.AddReadModels(opts => opts.ConnectionString = connectionString);
        services.AddSingleton<ICurrentTenantAccessor, StubTenantAccessor>();
        return services.BuildServiceProvider();
    }

    // One projection subscribing to one event, registered through the production AddProjection
    // so the container carries the same forwardings the real registration does.
    private static ServiceProvider ComposeProbeProjectionOnly()
    {
        var services = new ServiceCollection();
        services.AddProjection<ProbeProjection>();
        return services.BuildServiceProvider();
    }

    // The event-to-projection map read from the container, which is the only direction it
    // answers. Every registered IEventHandler in this tree is a projection.
    private static IReadOnlyList<string> NamesHandling<TEvent>(IServiceProvider provider)
        where TEvent : IDomainEvent
        => provider.GetServices<IEventHandler<TEvent>>()
            .Cast<IProjection>()
            .Select(p => p.Name)
            .ToList();

    private static Task<long> HeadAsync(NpgsqlDataSource dataSource)
        => new PostgresEventStoreHeadReader(new NpgsqlConnectionFactory(dataSource))
            .GetHeadPositionAsync(CancellationToken.None);

    // Moves the head off zero so a checkpoint at the head is a real assertion rather than the
    // empty-store coincidence of nothing being behind anything.
    private static async Task AppendProbeEventsAsync(NpgsqlDataSource dataSource)
    {
        var registry = new EventTypeRegistry().Register<WaiterProbe>();
        var eventStore = new PostgresEventStore(
            new NpgsqlConnectionFactory(dataSource),
            registry,
            new ProcessManagerEventTypeRegistry(),
            EventStoreJsonOptions.Create(),
            new EventUpcasterPipeline(registry, []));

        var stream = StreamId.Parse($"test:{Guid.NewGuid():N}");
        await eventStore.AppendAsync(stream, 0,
        [
            Envelope(stream, 1, new WaiterProbe(Guid.NewGuid())),
            Envelope(stream, 2, new WaiterProbe(Guid.NewGuid())),
            Envelope(stream, 3, new WaiterProbe(Guid.NewGuid())),
        ], CancellationToken.None);
    }

    // Advances one projection's checkpoint in its own committed transaction, the way a
    // projection handler does.
    private static async Task AdvanceAndCommitAsync(
        NpgsqlDataSource dataSource, string projectionName, long position)
    {
        var store = new PostgresCheckpointStore(new NpgsqlReadModelConnectionFactory(dataSource));
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await store.AdvanceAsync(projectionName, position, transaction, CancellationToken.None);
        await transaction.CommitAsync();
    }

    private static EventEnvelope Envelope(StreamId streamId, int version, IDomainEvent payload)
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

    // The hand-written doubles those facts pass in.

    // Public so the container's closed-generic resolution reaches them the way the replayer's
    // reflection reaches the catch-up service's test projection.
    public sealed record WaiterProbe(Guid Id) : IDomainEvent;

    public sealed record UnhandledProbe(Guid Id) : IDomainEvent;

    public sealed class ProbeProjection : IProjection, IEventHandler<WaiterProbe>
    {
        public const string ProjectionName = "waiter-probe";

        public string Name => ProjectionName;

        public Task HandleAsync(EventContext<WaiterProbe> context, CancellationToken ct)
            => Task.CompletedTask;
    }

    private sealed class FixedHeadPosition(long head) : IEventStoreHeadPosition
    {
        public Task<long> GetHeadPositionAsync(CancellationToken ct) => Task.FromResult(head);
    }

    // A checkpoint that never advances, so the head stays out of reach for the whole bound.
    private sealed class StuckCheckpointStore(long stuckAt) : ICheckpointStore
    {
        public Task<long> GetPositionAsync(string projectionName, CancellationToken ct)
            => Task.FromResult(stuckAt);

        public Task<long> GetPositionAsync(
            string projectionName, DbTransaction transaction, CancellationToken ct)
            => throw new NotSupportedException();

        public Task AdvanceAsync(
            string projectionName, long position, DbTransaction transaction, CancellationToken ct)
            => throw new NotSupportedException();

        public Task AdvanceAsync(string projectionName, long position, CancellationToken ct)
            => throw new NotSupportedException();
    }

    private sealed class ThrowingHeadPosition : IEventStoreHeadPosition
    {
        public Task<long> GetHeadPositionAsync(CancellationToken ct)
            => throw new InvalidOperationException(
                "The head must not be read when no projection handles the written events.");
    }

    private sealed class ThrowingCheckpointStore : ICheckpointStore
    {
        public Task<long> GetPositionAsync(string projectionName, CancellationToken ct)
            => throw new InvalidOperationException(
                "No checkpoint must be read when no projection handles the written events.");

        public Task<long> GetPositionAsync(
            string projectionName, DbTransaction transaction, CancellationToken ct)
            => throw new NotSupportedException();

        public Task AdvanceAsync(
            string projectionName, long position, DbTransaction transaction, CancellationToken ct)
            => throw new NotSupportedException();

        public Task AdvanceAsync(string projectionName, long position, CancellationToken ct)
            => throw new NotSupportedException();
    }

    // Advances on every read, so a deadline computed from it expires after a few polls with no
    // wall clock spent. GetUtcNow is the only member overridden, following the hand-rolled
    // provider in the SQL Server delay-queue tests.
    private sealed class AdvancingTimeProvider(DateTimeOffset start, TimeSpan step) : TimeProvider
    {
        private DateTimeOffset _now = start;

        public override DateTimeOffset GetUtcNow()
        {
            var current = _now;
            _now += step;
            return current;
        }
    }
}
