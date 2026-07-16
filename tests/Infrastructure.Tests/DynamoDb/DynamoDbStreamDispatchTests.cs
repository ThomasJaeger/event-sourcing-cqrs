using Amazon.DynamoDBStreams;
using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.EventStore.ContractTests;
using EventSourcingCqrs.Infrastructure.EventStore.DynamoDb;
using EventSourcingCqrs.Infrastructure.ReadModels.Postgres;
using EventSourcingCqrs.TestInfrastructure;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using Xunit;

namespace EventSourcingCqrs.Infrastructure.Tests.DynamoDb;

// RED for the DynamoDB Streams dispatch service, the native read-side mechanism that plays
// aggregate events into the in-process dispatcher in place of the relational outbox drain. Each
// fact composes a fresh table on the shared LocalStack node and a migrated PostgreSQL read-model
// database for the dispatch checkpoint, appends a known event shape, starts the service, and
// observes what the recording dispatcher received and how far the checkpoint advanced.
//
// Mirrors KurrentSubscriptionDispatchTests' drive-and-observe shape: start the hosted service, poll
// a recording double within a budget, tear down in a finally. The checkpoint is read and seeded
// through the existing ICheckpointStore surface, so the facts do not depend on how the GREEN loop
// advances it.
//
// How these ran RED, recorded rather than mythologized. The skeleton's ExecuteAsync threw, and
// BackgroundService.StartAsync does not propagate a faulted ExecuteAsync on this framework, not
// even a completed one, which this slice measured directly. The throw went unobserved on a
// background thread, so each fact went RED by dispatching nothing and failing its poll budget:
// RED for the intended reason, at the cost of a poll budget rather than an immediate throw.
//
// The first five facts below append before starting the service, so the startup drain alone
// satisfies them and they cannot tell a working wake from an inert one. The three at the bottom
// exist because of that: they append after the service settles and pin the stream as the trigger.
public class DynamoDbStreamDispatchTests
    : IClassFixture<LocalStackFixture>, IClassFixture<PostgresFixture>
{
    private static readonly TimeSpan PollBudget = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(100);

    // The settle probe watches for the startup drain's feed reads to stop climbing; the interval is
    // several EmptyShardBackoffs so a steady count means finished rather than merely between passes.
    private static readonly TimeSpan SettleInterval = TimeSpan.FromMilliseconds(800);

    // How long a fact watches a quiet or broken stream before concluding nothing happened. Wide
    // enough for many wake passes at the default 250ms backoff.
    private static readonly TimeSpan QuietBudget = TimeSpan.FromSeconds(3);

    private readonly LocalStackFixture _localStack;
    private readonly PostgresFixture _postgres;

    public DynamoDbStreamDispatchTests(LocalStackFixture localStack, PostgresFixture postgres)
    {
        _localStack = localStack;
        _postgres = postgres;
    }

    [Fact]
    public async Task Delivers_all_pre_existing_aggregate_events_in_position_order_from_an_empty_checkpoint()
    {
        // The catch-up drain. Events written before the service started are below any stream record
        // it will ever see, so this fact is what proves the checkpoint rather than the stream is
        // what the loop resumes from.
        await using var harness = await DynamoDbStreamHarness.CreateAsync(_localStack, _postgres);
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

        var dispatcher = new DynamoDbRecordingDispatcher();
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
    public async Task The_checkpoint_advances_past_every_dispatched_event()
    {
        await using var harness = await DynamoDbStreamHarness.CreateAsync(_localStack, _postgres);
        var stream = ContractEnvelopes.NewStreamId();
        await harness.Store.AppendAsync(stream, 0,
            [
                ContractEnvelopes.Build(stream, 1, new ContractOrderPlaced(Guid.NewGuid(), 10m)),
                ContractEnvelopes.Build(stream, 2, new ContractOrderNoted("second")),
            ],
            CancellationToken.None);

        var dispatcher = new DynamoDbRecordingDispatcher();
        var service = harness.CreateService(dispatcher);
        using var cts = new CancellationTokenSource();
        await service.StartAsync(cts.Token);
        try
        {
            var received = await PollForDispatchedAsync(dispatcher, 2);
            var highest = received.Max(m => m.GlobalPosition);
            var checkpoint = await PollForCheckpointAsync(harness, highest);

            checkpoint.Should().Be(highest,
                "the checkpoint carries what has been dispatched, so a restart resumes past it");
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Resumes_from_a_stored_checkpoint_without_redelivering_events_at_or_before_it()
    {
        // The resume contract. fromPosition is exclusive, so the event sitting exactly on the
        // checkpoint is not replayed.
        //
        // Above the checkpoint the delivery is at-least-once, not exactly-once: an event dispatched
        // but not yet checkpointed before a fault is redelivered on restart, and per-handler
        // idempotency absorbs it (projection GREATEST checkpoints, keyed process-manager dispatch).
        // This fact pins the floor, which is the half that a projection cannot absorb: an event
        // below the checkpoint arriving again would be reprocessed after the projection already
        // committed past it.
        await using var harness = await DynamoDbStreamHarness.CreateAsync(_localStack, _postgres);
        var first = ContractEnvelopes.NewStreamId();
        var second = ContractEnvelopes.NewStreamId();
        await harness.Store.AppendAsync(first, 0,
            [ContractEnvelopes.Build(first, 1, new ContractOrderPlaced(Guid.NewGuid(), 10m))],
            CancellationToken.None);
        await harness.Store.AppendAsync(second, 0,
            [ContractEnvelopes.Build(second, 1, new ContractOrderNoted("second"))],
            CancellationToken.None);

        var positions = await harness.ReadCommittedAggregatePositionsAsync();
        await harness.SeedCheckpointAsync(positions[0]);

        var dispatcher = new DynamoDbRecordingDispatcher();
        var service = harness.CreateService(dispatcher);
        using var cts = new CancellationTokenSource();
        await service.StartAsync(cts.Token);
        try
        {
            var received = await PollForDispatchedAsync(dispatcher, 1);
            received.Should().HaveCount(1);
            received[0].GlobalPosition.Should().Be(positions[1]);
            received.Select(m => m.GlobalPosition).Should().NotContain(positions[0],
                "the event sitting on the checkpoint is not replayed");
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Pm_events_never_reach_the_dispatcher()
    {
        // Projections derive workflow state from aggregate events and never from PM streams
        // (ADR 0013), so the drain's feed must not carry them. The PM row is in the log partition
        // and draws a position; ReadAllAsync is what excludes it.
        await using var harness = await DynamoDbStreamHarness.CreateAsync(_localStack, _postgres);
        var aggregate = ContractEnvelopes.NewStreamId();
        await harness.Store.AppendAsync(aggregate, 0,
            [ContractEnvelopes.Build(aggregate, 1, new ContractOrderPlaced(Guid.NewGuid(), 10m))],
            CancellationToken.None);

        var pmStream = ContractEnvelopes.NewProcessManagerStreamId();
        await harness.Store.AppendProcessManagerEventsAsync(pmStream, 0,
            [ContractEnvelopes.BuildProcessManager(pmStream, 1, new ContractStepRecorded(1))],
            CancellationToken.None);

        var dispatcher = new DynamoDbRecordingDispatcher();
        var service = harness.CreateService(dispatcher);
        using var cts = new CancellationTokenSource();
        await service.StartAsync(cts.Token);
        try
        {
            // Asserted by event type, because OutboxMessage carries no stream id: the dispatcher
            // seam's payload is the event and its metadata, and which stream it came from is not
            // part of what a handler sees. The Kurrent PM fact asserts the same way.
            var received = await PollForDispatchedAsync(dispatcher, 1);
            received.Should().ContainSingle().Which.EventType.Should().Be("ContractOrderPlaced");
            received.Should().NotContain(m => m.EventType == "ContractStepRecorded");
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task The_checkpoint_passes_a_filtered_stretch_when_the_next_aggregate_event_dispatches()
    {
        // Aggregate, PM, aggregate. The PM row sits between two dispatched events and holds a
        // position the drain skips, so the checkpoint has to cross it: after the second aggregate
        // event dispatches, the checkpoint sits at that event's position, above the PM row it never
        // delivered. The 0052 Option B shape, on this engine.
        //
        // Without this, a filtered row would pin the checkpoint below itself forever and every
        // restart would re-scan the stretch.
        await using var harness = await DynamoDbStreamHarness.CreateAsync(_localStack, _postgres);
        var first = ContractEnvelopes.NewStreamId();
        await harness.Store.AppendAsync(first, 0,
            [ContractEnvelopes.Build(first, 1, new ContractOrderPlaced(Guid.NewGuid(), 10m))],
            CancellationToken.None);

        var pmStream = ContractEnvelopes.NewProcessManagerStreamId();
        await harness.Store.AppendProcessManagerEventsAsync(pmStream, 0,
            [ContractEnvelopes.BuildProcessManager(pmStream, 1, new ContractStepRecorded(1))],
            CancellationToken.None);

        var second = ContractEnvelopes.NewStreamId();
        await harness.Store.AppendAsync(second, 0,
            [ContractEnvelopes.Build(second, 1, new ContractOrderNoted("after the pm row"))],
            CancellationToken.None);

        var aggregatePositions = await harness.ReadCommittedAggregatePositionsAsync();
        var allPositions = await harness.ReadCommittedPositionsAsync();
        allPositions.Should().HaveCount(3, "the PM row draws a position from the same counter");

        var dispatcher = new DynamoDbRecordingDispatcher();
        var service = harness.CreateService(dispatcher);
        using var cts = new CancellationTokenSource();
        await service.StartAsync(cts.Token);
        try
        {
            var received = await PollForDispatchedAsync(dispatcher, 2);
            received.Select(m => m.GlobalPosition).Should().Equal(aggregatePositions);

            var checkpoint = await PollForCheckpointAsync(harness, aggregatePositions[1]);
            checkpoint.Should().Be(aggregatePositions[1],
                "the checkpoint crosses the PM row it never dispatched");
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task A_record_returned_by_the_stream_is_what_triggers_the_delivering_drain()
    {
        // The trigger fact. Everything above appends before the service starts, so the startup
        // drain satisfies them and they cannot tell a working wake from an inert one. This appends
        // after the service has settled, and asserts on the order the loop did things: a non-empty
        // GetRecords precedes the feed read that delivered.
        //
        // The order is what makes it a trigger claim rather than a delivery claim. A loop that
        // simply re-drained forever would also deliver a post-start append, and that shape is the
        // table polling PLAN.md:475 forbids. Asserting that records came back first, and that the
        // feed was not read in between, is what separates the two.
        await using var harness = await DynamoDbStreamHarness.CreateAsync(_localStack, _postgres);
        var log = new DispatchObservationLog();
        var dispatcher = new DynamoDbRecordingDispatcher();
        var service = harness.CreateService(
            dispatcher,
            streams: new RecordingStreamsDecorator(harness.Streams, log),
            store: new CountingEventStore(harness.Store, log));

        using var cts = new CancellationTokenSource();
        await service.StartAsync(cts.Token);
        try
        {
            await SettleAsync(log);
            var settledReads = log.Count(DispatchObservation.FeedRead);

            var stream = ContractEnvelopes.NewStreamId();
            await harness.Store.AppendAsync(stream, 0,
                [ContractEnvelopes.Build(stream, 1, new ContractOrderPlaced(Guid.NewGuid(), 10m))],
                CancellationToken.None);

            var received = await PollForDispatchedAsync(dispatcher, 1);
            received.Should().HaveCount(1, "the append after settling still reaches the dispatcher");

            // The wake fired, and it fired before the drain that delivered.
            var after = log.Observations.Skip(SettledPrefixLength(log, settledReads)).ToList();
            after.Should().Contain(DispatchObservation.RecordsReturned,
                "the stream returned the record that woke the loop");
            var firstRecords = after.IndexOf(DispatchObservation.RecordsReturned);
            var firstRead = after.IndexOf(DispatchObservation.FeedRead);
            firstRead.Should().BeGreaterThan(firstRecords,
                "the feed is read because records arrived, not on a timer");
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Startup_acquires_its_shard_iterators_before_it_drains()
    {
        // The ordering that closes the startup window. Draining first and taking LATEST after
        // leaves an event committed between the two invisible to both halves: above the checkpoint
        // the drain just wrote, behind the iterator's LATEST point. It then stalls until an
        // unrelated later write wakes the loop, which on a quiet system is forever.
        //
        // What this pins is the ordering, not the race. Losing the race needs an append inside a
        // sub-millisecond window between two awaits, and hitting it deliberately would need a delay
        // seam in RunAsync between acquire and drain: production surface added purely for test
        // reach, in the hot path, which TDD_RULES section 3's preference order forbids. So this
        // fact asserts the property that makes the race unlosable rather than trying to lose it.
        // The other eight facts cannot see this: all of them are satisfied by a drain alone, and
        // the three wake facts settle before appending, which puts them well outside the window.
        await using var harness = await DynamoDbStreamHarness.CreateAsync(_localStack, _postgres);
        var log = new DispatchObservationLog();
        var dispatcher = new DynamoDbRecordingDispatcher();
        var service = harness.CreateService(
            dispatcher,
            streams: new RecordingStreamsDecorator(harness.Streams, log),
            store: new CountingEventStore(harness.Store, log));

        using var cts = new CancellationTokenSource();
        await service.StartAsync(cts.Token);
        try
        {
            await SettleAsync(log);

            var observations = log.Observations.ToList();
            var firstAcquire = observations.IndexOf(DispatchObservation.IteratorAcquired);
            var firstRead = observations.IndexOf(DispatchObservation.FeedRead);

            firstAcquire.Should().BeGreaterThanOrEqualTo(0, "startup acquires an iterator");
            firstRead.Should().BeGreaterThanOrEqualTo(0, "startup drains");
            firstAcquire.Should().BeLessThan(firstRead,
                "the iterator is in place before the drain reads, so an event committed during the "
                + "drain lands after the iterator point and wakes the loop rather than stalling");
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task A_quiet_stream_costs_no_read_of_the_event_feed()
    {
        // The no-poll fact (PLAN.md:475). Once the startup drain finishes, a healthy but silent
        // stream must not cause another feed read: the loop asks the stream, the stream says
        // nothing, and the event table is left alone. A loop that re-drained on a timer would fail
        // here, which is exactly the shape "without polling" forbids.
        await using var harness = await DynamoDbStreamHarness.CreateAsync(_localStack, _postgres);
        var log = new DispatchObservationLog();
        var dispatcher = new DynamoDbRecordingDispatcher();
        var service = harness.CreateService(
            dispatcher,
            streams: new RecordingStreamsDecorator(harness.Streams, log),
            store: new CountingEventStore(harness.Store, log));

        using var cts = new CancellationTokenSource();
        await service.StartAsync(cts.Token);
        try
        {
            await SettleAsync(log);
            var settledReads = log.Count(DispatchObservation.FeedRead);

            await Task.Delay(QuietBudget);

            log.Count(DispatchObservation.FeedRead).Should().Be(settledReads,
                "a quiet stream wakes nothing, so the event feed is never read");
            log.Count(DispatchObservation.RecordsEmpty).Should().BeGreaterThan(0,
                "the loop did keep asking the stream, so it was quiet rather than stopped");
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task A_broken_stream_delivers_nothing_within_the_reconnect_period()
    {
        // The fault fact: proves the wake is load-bearing rather than decorative by removing only
        // the wake. Every GetRecords throws; everything else reaches LocalStack.
        //
        // ReconnectBackoff is set past the budget on purpose. The loop's fault restart drains, and
        // that restart drain is real degraded-mode availability rather than a defect: with a broken
        // stream, events still reach projections once per backoff. This fact holds the backoff open
        // so the budget lands inside one restart period, which is where the wake's absence is
        // visible. It pins that the stream is the trigger, not that a broken stream loses events.
        await using var harness = await DynamoDbStreamHarness.CreateAsync(_localStack, _postgres);
        var log = new DispatchObservationLog();
        var dispatcher = new DynamoDbRecordingDispatcher();
        var service = harness.CreateService(
            dispatcher,
            streams: new FaultingStreamsDecorator(harness.Streams),
            store: new CountingEventStore(harness.Store, log),
            options: new DynamoDbStreamDispatchOptions
            {
                ReconnectBackoff = TimeSpan.FromMinutes(5),
            });

        using var cts = new CancellationTokenSource();
        await service.StartAsync(cts.Token);
        try
        {
            await SettleAsync(log);
            var settledReads = log.Count(DispatchObservation.FeedRead);

            var stream = ContractEnvelopes.NewStreamId();
            await harness.Store.AppendAsync(stream, 0,
                [ContractEnvelopes.Build(stream, 1, new ContractOrderPlaced(Guid.NewGuid(), 10m))],
                CancellationToken.None);

            await Task.Delay(QuietBudget);

            dispatcher.Received.Should().BeEmpty(
                "with no wake, nothing tells the loop the append happened");
            log.Count(DispatchObservation.FeedRead).Should().Be(settledReads,
                "and no drain runs beyond the startup one");
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    // Waits for the startup drain to finish: it reads the feed at least once, and settles when the
    // reads stop climbing. The loop reads the feed only from the drain, so a steady count is a
    // finished drain.
    private static async Task SettleAsync(DispatchObservationLog log)
    {
        var deadline = DateTime.UtcNow + PollBudget;
        var last = -1;
        while (DateTime.UtcNow < deadline)
        {
            var reads = log.Count(DispatchObservation.FeedRead);
            if (reads > 0 && reads == last)
            {
                return;
            }
            last = reads;
            await Task.Delay(SettleInterval);
        }
    }

    // Where the settled prefix ends, so a fact can assert on what the loop did afterwards.
    private static int SettledPrefixLength(DispatchObservationLog log, int settledReads)
    {
        var seen = 0;
        var observations = log.Observations;
        for (var i = 0; i < observations.Count; i++)
        {
            if (observations[i] == DispatchObservation.FeedRead && ++seen == settledReads)
            {
                return i + 1;
            }
        }
        return observations.Count;
    }

    private static async Task<IReadOnlyList<OutboxMessage>> PollForDispatchedAsync(
        DynamoDbRecordingDispatcher dispatcher, int expected)
    {
        var deadline = DateTime.UtcNow + PollBudget;
        while (DateTime.UtcNow < deadline)
        {
            var received = dispatcher.Received;
            if (received.Count >= expected)
            {
                return received;
            }
            await Task.Delay(PollInterval);
        }
        return dispatcher.Received;
    }

    private static async Task<long> PollForCheckpointAsync(DynamoDbStreamHarness harness, long expected)
    {
        var deadline = DateTime.UtcNow + PollBudget;
        while (DateTime.UtcNow < deadline)
        {
            var checkpoint = await harness.ReadCheckpointAsync();
            if (checkpoint >= expected)
            {
                return checkpoint;
            }
            await Task.Delay(PollInterval);
        }
        return await harness.ReadCheckpointAsync();
    }
}

// Records every dispatched message, thread-safe because the dispatch loop runs on a background
// thread while the fact polls from the test thread.
internal sealed class DynamoDbRecordingDispatcher : IMessageDispatcher
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

// One table and one read-model database per fact, torn down with the fact. The store composes
// through the same AddDynamoDbEventStore seam a host uses, so appends serialize exactly as
// production does; the checkpoint store is the real PostgreSQL one against the migrated read-model
// database.
internal sealed class DynamoDbStreamHarness : IAsyncDisposable
{
    private readonly ServiceProvider _provider;
    private readonly NpgsqlReadModelConnectionFactory _readModelFactory;
    private readonly Amazon.DynamoDBv2.IAmazonDynamoDB _client;
    private readonly IAmazonDynamoDBStreams _streams;
    private readonly string _tableName;

    private DynamoDbStreamHarness(
        ServiceProvider provider,
        Amazon.DynamoDBv2.IAmazonDynamoDB client,
        IAmazonDynamoDBStreams streams,
        string tableName,
        NpgsqlReadModelConnectionFactory readModelFactory,
        IEventStore store,
        ICheckpointStore checkpointStore)
    {
        _provider = provider;
        _client = client;
        _streams = streams;
        _tableName = tableName;
        _readModelFactory = readModelFactory;
        Store = store;
        CheckpointStore = checkpointStore;
    }

    public IEventStore Store { get; }

    public ICheckpointStore CheckpointStore { get; }

    public static async Task<DynamoDbStreamHarness> CreateAsync(
        LocalStackFixture localStack, PostgresFixture postgres)
    {
        var tableName = LocalStackFixture.NewTableName();

        var services = new ServiceCollection();
        services.AddSingleton<IEventTypeProvider, ContractDomainEventTypeProvider>();
        services.AddSingleton<IProcessManagerEventTypeProvider, ContractProcessManagerEventTypeProvider>();
        services.AddDynamoDbEventStore(opts =>
        {
            opts.ServiceUrl = localStack.ServiceUrl;
            opts.TableName = tableName;
            opts.Region = "us-east-1";
            opts.AccessKeyId = "test";
            opts.SecretAccessKey = "test";
        });
        services.AddDynamoDbStreamDispatchService();

        var provider = services.BuildServiceProvider();
        try
        {
            var store = provider.GetRequiredService<IEventStore>();
            var client = provider.GetRequiredService<Amazon.DynamoDBv2.IAmazonDynamoDB>();
            var streams = provider.GetRequiredService<IAmazonDynamoDBStreams>();
            await provider.GetRequiredService<DynamoDbTableProvisioner>()
                .EnsureTableAsync(CancellationToken.None);

            var readModelConnectionString = await postgres.CreateMigratedDatabaseAsync();
            var readModelFactory = new NpgsqlReadModelConnectionFactory(
                NpgsqlDataSource.Create(readModelConnectionString));
            var checkpointStore = new PostgresCheckpointStore(readModelFactory);

            return new DynamoDbStreamHarness(
                provider, client, streams, tableName, readModelFactory, store, checkpointStore);
        }
        catch
        {
            await provider.DisposeAsync();
            throw;
        }
    }

    // Built by hand rather than resolved, so a fact can hand in its own recording dispatcher and,
    // where it needs to observe or break the wake, its own decorated Streams client and event
    // store. The seams are the constructor's own parameters; nothing is reached into.
    public DynamoDbStreamDispatchService CreateService(
        IMessageDispatcher dispatcher,
        IAmazonDynamoDBStreams? streams = null,
        IEventStore? store = null,
        DynamoDbStreamDispatchOptions? options = null)
        => new(
            streams ?? _streams,
            store ?? Store,
            CheckpointStore,
            dispatcher,
            Options.Create(new DynamoDbEventStoreOptions { TableName = _tableName }),
            Options.Create(options ?? new DynamoDbStreamDispatchOptions()),
            NullLogger<DynamoDbStreamDispatchService>.Instance);

    // The live Streams client the harness composed, for a fact that wants to decorate it.
    public IAmazonDynamoDBStreams Streams => _streams;

    // Every committed position on the log partition, PM rows included: the stretch a dispatch
    // checkpoint has to advance across spans both append paths.
    public async Task<IReadOnlyList<long>> ReadCommittedPositionsAsync()
    {
        var page = await _client.QueryAsync(new Amazon.DynamoDBv2.Model.QueryRequest
        {
            TableName = _tableName,
            KeyConditionExpression = "pk = :pk",
            ExpressionAttributeValues = new()
            {
                [":pk"] = new Amazon.DynamoDBv2.Model.AttributeValue { S = "$log" },
            },
            ConsistentRead = true,
            ScanIndexForward = true,
        });
        return page.Items.Select(i => long.Parse(i["sk"].N)).ToList();
    }

    // Aggregate-only committed positions, the ones a dispatch carries.
    public async Task<IReadOnlyList<long>> ReadCommittedAggregatePositionsAsync()
    {
        var feed = new List<long>();
        await foreach (var e in Store.ReadAllAsync(0, CancellationToken.None))
        {
            feed.Add(e.GlobalPosition);
        }
        return feed;
    }

    public async Task SeedCheckpointAsync(long position)
        => await CheckpointStore.AdvanceAsync(
            DynamoDbStreamDispatchService.CheckpointName, position, CancellationToken.None);

    public Task<long> ReadCheckpointAsync()
        => CheckpointStore.GetPositionAsync(
            DynamoDbStreamDispatchService.CheckpointName, CancellationToken.None);

    public async ValueTask DisposeAsync()
    {
        try
        {
            await _client.DeleteTableAsync(_tableName);
        }
        catch (Amazon.DynamoDBv2.Model.ResourceNotFoundException)
        {
            // Never provisioned, or already gone.
        }

        await _readModelFactory.DisposeAsync();
        await _provider.DisposeAsync();
    }
}
