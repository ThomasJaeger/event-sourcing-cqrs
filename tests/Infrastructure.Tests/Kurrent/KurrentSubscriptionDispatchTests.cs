using System.Text.Json;
using DotNet.Testcontainers.Containers;
using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.EventStore.ContractTests;
using EventSourcingCqrs.Infrastructure.EventStore.Kurrent;
using EventSourcingCqrs.Infrastructure.Versioning;
using EventSourcingCqrs.Infrastructure.ReadModels.Postgres;
using EventSourcingCqrs.TestInfrastructure;
using FluentAssertions;
using KurrentDB.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using Xunit;

namespace EventSourcingCqrs.Infrastructure.Tests.Kurrent;

// RED for slice 4's KurrentDB subscription dispatch service, the native read-side mechanism that plays
// aggregate events into the in-process dispatcher in place of the relational outbox drain. Each fact
// composes a fresh KurrentDB node for the $all feed and a migrated PostgreSQL read-model database for
// the dispatch checkpoint, appends a known event shape, starts the service, and observes what the
// recording dispatcher received and how far the checkpoint advanced. This mirrors the outbox
// notification tests' drive-and-observe shape: start the hosted service, poll a recording double
// within a budget, tear down in a finally.
//
// How these ran RED, corrected. This header used to claim the facts failed on the skeleton's
// NotImplementedException, and they cannot have: BackgroundService.StartAsync does not propagate a
// faulted ExecuteAsync on this framework, not even a completed one, which Phase 14 measured
// directly while writing the DynamoDB dispatcher's own RED. The skeleton's throw went unobserved on
// a background thread, so each fact went RED by dispatching nothing and failing its poll budget.
// That is still RED for the intended reason, the asserted behavior missing, and it is worth knowing
// that a hosted service's RED costs a poll budget rather than an immediate throw.
//
// The checkpoint is read and seeded through the existing ICheckpointStore surface, so the facts do
// not depend on how the loop advances it.
public class KurrentSubscriptionDispatchTests
    : IClassFixture<KurrentFixture>, IClassFixture<PostgresFixture>
{
    private static readonly TimeSpan PollBudget = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(100);

    private readonly KurrentFixture _kurrent;
    private readonly PostgresFixture _postgres;

    public KurrentSubscriptionDispatchTests(KurrentFixture kurrent, PostgresFixture postgres)
    {
        _kurrent = kurrent;
        _postgres = postgres;
    }

    [Fact]
    public async Task Delivers_all_pre_existing_aggregate_events_in_order_from_an_empty_checkpoint()
    {
        await using var harness = await KurrentSubscriptionHarness.CreateAsync(_kurrent, _postgres);
        var first = ContractEnvelopes.NewStreamId();
        var second = ContractEnvelopes.NewStreamId();
        var firstId = Guid.NewGuid();
        var secondId = Guid.NewGuid();
        await harness.Store.AppendAsync(first, 0,
            [ContractEnvelopes.Build(first, 1, new ContractOrderPlaced(Guid.NewGuid(), 10m), firstId)],
            CancellationToken.None);
        await harness.Store.AppendAsync(second, 0,
            [ContractEnvelopes.Build(second, 1, new ContractOrderNoted("second"), secondId)],
            CancellationToken.None);

        var dispatcher = new KurrentRecordingDispatcher();
        var service = harness.CreateService(dispatcher);
        using var cts = new CancellationTokenSource();
        await service.StartAsync(cts.Token);
        try
        {
            var received = await PollForDispatchedAsync(dispatcher, 2);
            received.Select(m => m.EventType)
                .Should().ContainInOrder("ContractOrderPlaced", "ContractOrderNoted");
            received.Select(m => m.EventId).Should().ContainInOrder(firstId, secondId);
            received.Select(m => m.GlobalPosition).Should().BeInAscendingOrder();
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Resumes_from_a_stored_checkpoint_without_redelivering_events_at_or_before_it()
    {
        await using var harness = await KurrentSubscriptionHarness.CreateAsync(_kurrent, _postgres);
        var first = ContractEnvelopes.NewStreamId();
        var second = ContractEnvelopes.NewStreamId();
        await harness.Store.AppendAsync(first, 0,
            [ContractEnvelopes.Build(first, 1, new ContractOrderPlaced(Guid.NewGuid(), 1m))],
            CancellationToken.None);
        await harness.Store.AppendAsync(second, 0,
            [ContractEnvelopes.Build(second, 1, new ContractOrderNoted("after"))],
            CancellationToken.None);

        // Seed the checkpoint at the first event's committed position; the second is the only one past
        // it, so resuming exclusively from the checkpoint dispatches the second and never the first.
        var positions = await harness.ReadCommittedAggregatePositionsAsync();
        await harness.SeedCheckpointAsync(positions[0]);

        var dispatcher = new KurrentRecordingDispatcher();
        var service = harness.CreateService(dispatcher);
        using var cts = new CancellationTokenSource();
        await service.StartAsync(cts.Token);
        try
        {
            var received = await PollForDispatchedAsync(dispatcher, 1);
            received.Should().ContainSingle().Which.EventType.Should().Be("ContractOrderNoted");
            received.Should().OnlyContain(m => m.GlobalPosition > positions[0]);
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    // A filtered stretch in the middle of the log: aggregate1, a run of PM events, then aggregate2.
    // Dispatching aggregate2 advances the checkpoint past every PM position through the per-event
    // advance, and no PM event dispatches. This tests the checkpoint passing a filtered stretch; it
    // does not test a filtered TAIL, because KurrentDB advances the checkpoint past a tail only when
    // the server emits a checkpoint message, which below its catch-up batch threshold it does not. That
    // tail-lag characteristic is documented at KurrentSubscriptionService and in ADR 0047.
    [Fact]
    public async Task The_checkpoint_passes_a_filtered_stretch_when_the_next_aggregate_event_dispatches()
    {
        await using var harness = await KurrentSubscriptionHarness.CreateAsync(_kurrent, _postgres);
        var first = ContractEnvelopes.NewStreamId();
        await harness.Store.AppendAsync(first, 0,
            [ContractEnvelopes.Build(first, 1, new ContractOrderPlaced(Guid.NewGuid(), 1m))],
            CancellationToken.None);
        var pm = ContractEnvelopes.NewProcessManagerStreamId();
        await harness.Store.AppendProcessManagerEventsAsync(pm, 0,
            [
                ContractEnvelopes.BuildProcessManager(pm, 1, new ContractStepRecorded(1)),
                ContractEnvelopes.BuildProcessManager(pm, 2, new ContractStepRecorded(2)),
                ContractEnvelopes.BuildProcessManager(pm, 3, new ContractStepRecorded(3)),
            ],
            CancellationToken.None);
        var second = ContractEnvelopes.NewStreamId();
        await harness.Store.AppendAsync(second, 0,
            [ContractEnvelopes.Build(second, 1, new ContractOrderNoted("after the stretch"))],
            CancellationToken.None);
        var secondPosition = (await harness.ReadCommittedAggregatePositionsAsync()).Max();

        var dispatcher = new KurrentRecordingDispatcher();
        var service = harness.CreateService(dispatcher);
        using var cts = new CancellationTokenSource();
        await service.StartAsync(cts.Token);
        try
        {
            var received = await PollForDispatchedAsync(dispatcher, 2);
            received.Select(m => m.EventType)
                .Should().Equal("ContractOrderPlaced", "ContractOrderNoted");
            received.Should().NotContain(m => m.EventType == "ContractStepRecorded");

            // The checkpoint reaches aggregate2's position, which sits beyond every PM position, so it
            // has passed the whole filtered stretch.
            var checkpoint = await PollForCheckpointAtLeastAsync(harness, secondPosition);
            checkpoint.Should().BeGreaterThanOrEqualTo(secondPosition);
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Pm_and_system_events_never_reach_the_dispatcher()
    {
        await using var harness = await KurrentSubscriptionHarness.CreateAsync(_kurrent, _postgres);
        var aggregate = ContractEnvelopes.NewStreamId();
        await harness.Store.AppendAsync(aggregate, 0,
            [ContractEnvelopes.Build(aggregate, 1, new ContractOrderPlaced(Guid.NewGuid(), 1m))],
            CancellationToken.None);
        var pm = ContractEnvelopes.NewProcessManagerStreamId();
        await harness.Store.AppendProcessManagerEventsAsync(pm, 0,
            [ContractEnvelopes.BuildProcessManager(pm, 1, new ContractStepRecorded(1))],
            CancellationToken.None);

        var dispatcher = new KurrentRecordingDispatcher();
        var service = harness.CreateService(dispatcher);
        using var cts = new CancellationTokenSource();
        await service.StartAsync(cts.Token);
        try
        {
            var received = await PollForDispatchedAsync(dispatcher, 1);
            received.Should().ContainSingle().Which.EventType.Should().Be("ContractOrderPlaced");
            received.Should().NotContain(m => m.EventType == "ContractStepRecorded");
            received.Should().NotContain(m => m.EventType.StartsWith('$'));
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    // Reach: pure ResolvedEvent construction has no precedent on disk (EventRecord is constructed
    // nowhere in src or tests, so the Kurrent adapter's own tests are all container-backed). Per
    // TDD_RULES section 3 the reach is option 1, the existing injected harness: the subscription
    // service calls ToOutboxMessage, and the recording dispatcher observes the carry-through.
    [Fact]
    public async Task Dispatched_message_carries_the_stored_event_version()
    {
        await using var harness = await KurrentSubscriptionHarness.CreateAsync(_kurrent, _postgres);
        var stream = ContractEnvelopes.NewStreamId();
        await harness.Store.AppendAsync(stream, 0,
            [ContractEnvelopes.Build(stream, 1, new ContractOrderPlaced(Guid.NewGuid(), 1m))],
            CancellationToken.None);

        var dispatcher = new KurrentRecordingDispatcher();
        var service = harness.CreateService(dispatcher);
        using var cts = new CancellationTokenSource();
        await service.StartAsync(cts.Token);
        try
        {
            var received = await PollForDispatchedAsync(dispatcher, 1);
            received.Should().ContainSingle();
            received[0].EventVersion.Should().Be(1);
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    // The two Kurrent hydration helpers, characterized against a stored version-1 event. These are
    // green-on-write and carry no RED of their own: the upcaster mechanism was RED-proven at the V1
    // unit level (EventUpcasterPipelineTests) and its threading at the V2a composition level
    // (EventUpcasterPipelineCompositionTests). What these pin is that ToEventEnvelope (the read path)
    // and ToOutboxMessage (the dispatch path) each apply the composed pipeline. Reach is the sibling
    // version-carry fact's reach exactly: ResolvedEvent is not constructible on disk, so the harness
    // drives the helpers rather than a pure unit calling them.
    [Fact]
    public async Task Read_lifts_a_version_one_stored_event_to_the_current_shape()
    {
        await using var harness = await KurrentSubscriptionHarness.CreateAsync(_kurrent, _postgres);
        var stream = ContractEnvelopes.NewStreamId();
        var itemId = Guid.NewGuid();
        await harness.SeedVersionOneAsync(
            stream, nameof(ContractItemArchived), new ContractItemArchivedV1(itemId));

        // ReadStreamAsync hydrates through ToEventEnvelope.
        var read = await harness.Store.ReadStreamAsync(stream, 0, CancellationToken.None);

        read.Should().ContainSingle();
        var upcast = read[0].Payload.Should().BeOfType<ContractItemArchived>().Which;
        upcast.ItemId.Should().Be(itemId);
        upcast.Revision.Should().Be(1);
        read[0].EventVersion.Should().Be(2);
    }

    [Fact]
    public async Task Dispatch_lifts_a_version_one_stored_event_to_the_current_shape()
    {
        await using var harness = await KurrentSubscriptionHarness.CreateAsync(_kurrent, _postgres);
        var stream = ContractEnvelopes.NewStreamId();
        var itemId = Guid.NewGuid();
        await harness.SeedVersionOneAsync(
            stream, nameof(ContractItemArchived), new ContractItemArchivedV1(itemId));

        // The subscription dispatch hydrates through ToOutboxMessage.
        var dispatcher = new KurrentRecordingDispatcher();
        var service = harness.CreateService(dispatcher);
        using var cts = new CancellationTokenSource();
        await service.StartAsync(cts.Token);
        try
        {
            var received = await PollForDispatchedAsync(dispatcher, 1);
            received.Should().ContainSingle();
            var upcast = received[0].Event.Should().BeOfType<ContractItemArchived>().Which;
            upcast.ItemId.Should().Be(itemId);
            upcast.Revision.Should().Be(1);
            received[0].EventVersion.Should().Be(2);
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    private static async Task<IReadOnlyList<OutboxMessage>> PollForDispatchedAsync(
        KurrentRecordingDispatcher dispatcher, int expected)
    {
        var deadline = DateTime.UtcNow + PollBudget;
        while (DateTime.UtcNow < deadline && dispatcher.Received.Count < expected)
        {
            await Task.Delay(PollInterval);
        }
        return dispatcher.Received;
    }

    private static async Task<long> PollForCheckpointAtLeastAsync(
        KurrentSubscriptionHarness harness, long target)
    {
        var deadline = DateTime.UtcNow + PollBudget;
        var checkpoint = await harness.ReadCheckpointAsync();
        while (DateTime.UtcNow < deadline && checkpoint < target)
        {
            await Task.Delay(PollInterval);
            checkpoint = await harness.ReadCheckpointAsync();
        }
        return checkpoint;
    }
}

// Records every dispatched message, thread-safe because the subscription loop dispatches from a
// background thread while the fact polls from the test thread.
internal sealed class KurrentRecordingDispatcher : IMessageDispatcher
{
    private readonly object _gate = new();
    private readonly List<OutboxMessage> _received = [];

    public IReadOnlyList<OutboxMessage> Received
    {
        get
        {
            lock (_gate)
            {
                return _received.ToList();
            }
        }
    }

    public Task DispatchAsync(OutboxMessage message, CancellationToken ct)
    {
        lock (_gate)
        {
            _received.Add(message);
        }
        return Task.CompletedTask;
    }
}

// One node and one read-model database per fact, torn down with the fact. The store composes through
// the same AddKurrentEventStore seam a host uses (so appends serialize exactly as production does);
// the client is the singleton the store holds, handed to the subscription service; the checkpoint
// store is the real PostgreSQL one against the migrated read-model database.
internal sealed class KurrentSubscriptionHarness : IAsyncDisposable
{
    private readonly IContainer _container;
    private readonly ServiceProvider _provider;
    private readonly NpgsqlReadModelConnectionFactory _readModelFactory;
    private readonly KurrentDBClient _client;
    private readonly EventUpcasterPipeline _pipeline;
    private readonly JsonSerializerOptions _jsonOptions;

    private KurrentSubscriptionHarness(
        IContainer container,
        ServiceProvider provider,
        KurrentDBClient client,
        EventUpcasterPipeline pipeline,
        JsonSerializerOptions jsonOptions,
        NpgsqlReadModelConnectionFactory readModelFactory,
        IEventStore store,
        ICheckpointStore checkpointStore)
    {
        _container = container;
        _provider = provider;
        _client = client;
        _pipeline = pipeline;
        _jsonOptions = jsonOptions;
        _readModelFactory = readModelFactory;
        Store = store;
        CheckpointStore = checkpointStore;
    }

    public IEventStore Store { get; }

    public ICheckpointStore CheckpointStore { get; }

    public static async Task<KurrentSubscriptionHarness> CreateAsync(
        KurrentFixture kurrent, PostgresFixture postgres)
    {
        var container = await kurrent.StartNodeAsync();
        try
        {
            var connectionString =
                $"esdb://{container.Hostname}:{container.GetMappedPublicPort(2113)}?tls=false";

            var services = new ServiceCollection();
            services.AddSingleton<IEventTypeProvider, ContractDomainEventTypeProvider>();
            services.AddSingleton<IProcessManagerEventTypeProvider, ContractProcessManagerEventTypeProvider>();
            // The upcaster reaches the composed pipeline the way a host contributes one, so the
            // dispatch and read paths this harness drives lift a stored V1 event to the current shape.
            services.AddSingleton<IEventUpcaster, ContractItemArchivedV1ToV2>();
            services.AddKurrentEventStore(opts => opts.ConnectionString = connectionString);
            var provider = services.BuildServiceProvider();
            try
            {
                var store = provider.GetRequiredService<IEventStore>();
                var client = provider.GetRequiredService<KurrentDBClient>();
                var pipeline = provider.GetRequiredService<EventUpcasterPipeline>();
                var jsonOptions = provider.GetRequiredService<JsonSerializerOptions>();
                var readModelConnectionString = await postgres.CreateMigratedDatabaseAsync();
                var readModelFactory = new NpgsqlReadModelConnectionFactory(
                    NpgsqlDataSource.Create(readModelConnectionString));
                var checkpointStore = new PostgresCheckpointStore(readModelFactory);
                return new KurrentSubscriptionHarness(
                    container, provider, client, pipeline, jsonOptions, readModelFactory,
                    store, checkpointStore);
            }
            catch
            {
                await provider.DisposeAsync();
                throw;
            }
        }
        catch
        {
            await container.DisposeAsync();
            throw;
        }
    }

    public KurrentSubscriptionService CreateService(IMessageDispatcher dispatcher)
        => new(
            _client,
            _jsonOptions,
            CheckpointStore,
            dispatcher,
            Options.Create(new KurrentSubscriptionOptions()),
            NullLogger<KurrentSubscriptionService>.Instance,
            _pipeline);

    // Every committed position on the $all feed excluding system events, PM streams included: the
    // stretch a dispatch checkpoint has to advance across spans both append paths.
    public async Task<IReadOnlyList<long>> ReadCommittedPositionsAsync()
    {
        var positions = new List<long>();
        await foreach (var resolved in _client.ReadAllAsync(Direction.Forwards, Position.Start))
        {
            if (resolved.OriginalStreamId.StartsWith('$') || resolved.Event.EventType.StartsWith('$'))
            {
                continue;
            }
            positions.Add(checked((long)resolved.Event.Position.CommitPosition));
        }
        return positions;
    }

    // Aggregate-only committed positions, the ones a dispatch carries.
    public async Task<IReadOnlyList<long>> ReadCommittedAggregatePositionsAsync()
    {
        var positions = new List<long>();
        await foreach (var resolved in _client.ReadAllAsync(Direction.Forwards, Position.Start))
        {
            if (resolved.OriginalStreamId.StartsWith('$')
                || resolved.OriginalStreamId.StartsWith("pm-", StringComparison.Ordinal)
                || resolved.Event.EventType.StartsWith('$'))
            {
                continue;
            }
            positions.Add(checked((long)resolved.Event.Position.CommitPosition));
        }
        return positions;
    }

    // A historical write, reproduced through the adapter's own append with a throwaway registry that
    // maps the V1 shape onto the terminal's stable storage name, so the stored event lands at schema
    // version 1 with the V1 payload. The harness's own store and subscription then hydrate it through
    // the real pipeline.
    public async Task SeedVersionOneAsync(
        StreamId stream, string terminalStorageName, IDomainEvent versionOnePayload)
    {
        var seedRegistry = new EventTypeRegistry().Register(
            versionOnePayload.GetType(), terminalStorageName);
        var seedStore = new KurrentEventStore(
            _client,
            seedRegistry,
            new ProcessManagerEventTypeRegistry(),
            _jsonOptions,
            new EventUpcasterPipeline(seedRegistry, []));
        await seedStore.AppendAsync(
            stream, 0, [ContractEnvelopes.Build(stream, 1, versionOnePayload)], CancellationToken.None);
    }

    public async Task SeedCheckpointAsync(long position)
    {
        await using var connection = await _readModelFactory.OpenConnectionAsync(CancellationToken.None);
        await using var transaction = await connection.BeginTransactionAsync(CancellationToken.None);
        await CheckpointStore.AdvanceAsync(
            KurrentSubscriptionService.CheckpointName, position, transaction, CancellationToken.None);
        await transaction.CommitAsync(CancellationToken.None);
    }

    public Task<long> ReadCheckpointAsync()
        => CheckpointStore.GetPositionAsync(
            KurrentSubscriptionService.CheckpointName, CancellationToken.None);

    public async ValueTask DisposeAsync()
    {
        await _readModelFactory.DisposeAsync();
        await _provider.DisposeAsync();
        await _container.DisposeAsync();
    }
}
