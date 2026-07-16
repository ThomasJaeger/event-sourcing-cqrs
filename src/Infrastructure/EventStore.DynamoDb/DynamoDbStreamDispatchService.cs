using System.Diagnostics;
using Amazon.DynamoDBStreams;
using Amazon.DynamoDBStreams.Model;
using EventSourcingCqrs.Domain.Abstractions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace EventSourcingCqrs.Infrastructure.EventStore.DynamoDb;

// The DynamoDB read-side dispatch mechanism: a Streams consumer that wakes on the table's change
// feed and plays aggregate events into the in-process dispatcher, the managed-cloud counterpart to
// the relational outbox processor and to KurrentDB's catch-up subscription (PLAN.md:465, :475).
//
// WAKE-THEN-DRAIN, and this is the ruling the whole design turns on. A stream record is a wake
// signal and nothing more. The loop never parses one; it reads IEventStore.ReadAllAsync from the
// stored checkpoint and dispatches what the log says, in position order. KurrentDB's subscription
// can dispatch the record itself because its $all feed is the ordered log. DynamoDB Streams is not:
// it is a per-shard feed over table mutations, ordered within a shard and unordered across shards,
// and this table's writes land under several partition keys per append (the event row, the log row,
// the counter, the id row). Dispatching records directly would deliver an append's rows in shard
// order rather than position order, interleave the counter's own updates with events, and hand a
// projection a feed whose order depends on how DynamoDB happened to split its shards.
//
// Because the record is only a signal, the table's stream is provisioned KEYS_ONLY: an image would
// cross the wire and be dropped.
//
// THE CHECKPOINT IS TRUTH; THE STREAM NEVER IS. A shard iterator's sequence number is never stored
// and never resumed from. Sequence numbers are 56-character opaque strings, scoped to a shard, and
// a shard's lifetime is the engine's to decide: shards split, close, and expire, and a stored
// sequence number outlives none of that reliably. A stream's records also expire (24 hours on AWS,
// and a spike found LocalStack's TRIM_HORIZON returning only the tail of what was written). The
// global position in the log partition has none of those properties: it is a long, it is total, and
// it is durable for as long as the events are. So the checkpoint stores a position, the drain reads
// from it, and the stream contributes only the news that there is something to read.
//
// ITERATORS FIRST, THEN THE DRAIN, and the order is load-bearing. Draining first and taking LATEST
// after leaves a window: an event committed between the drain's last read and the GetShardIterator
// call sits above the checkpoint the drain just wrote, and behind the iterator's LATEST point, so
// neither half can see it and it stalls until some unrelated later write wakes the loop. On a quiet
// system that is unbounded. Acquiring first inverts the overlap: an event committed during the
// drain lands after the iterator point, so it wakes the loop, and the drain that follows the wake
// finds it has already been dispatched and returns. The cost is a redundant wake, which is free
// because the drain is idempotent and a drain with nothing to do is one ReadAllAsync that yields
// nothing.
//
// EVERY NEWLY ACQUIRED ITERATOR FORCES A DRAIN. LATEST means "records after this moment", so the
// stretch between whatever the iterator skipped and now is invisible to the stream by construction.
// That applies at start, at fault restart, when an expired iterator is replaced, and when a split's
// child shard is discovered. There is exactly one rule and no path may leave an acquisition
// undrained: the drain is the only thing that can find those rows, and it is cheap enough that
// draining on suspicion is always the right trade.
//
// "Without polling" (PLAN.md:475) reads as: no interval poll of the event table for new events, the
// way the relational outbox processors poll their outbox. The change feed is the trigger. A quiet
// feed costs one GetRecords per live shard per EmptyShardBackoff and touches the event table not at
// all. Shard discovery is a DescribeStream and sits on ShardRefreshInterval rather than the wake
// loop, so the control-plane call stays off the hot path.
//
// The fault-restart drain is degraded-mode availability, not a second trigger. If the stream fails
// outright, the loop still restarts every ReconnectBackoff and each restart drains, so events keep
// reaching projections at that period rather than not at all. That is a floor under a broken
// stream, bounded and coarse; it is not how events normally arrive, and a deployment whose stream
// is down is degraded rather than fine.
//
// At-least-once, like every other dispatch path here: an event dispatched but not yet checkpointed
// before a fault is redelivered on restart, and per-handler idempotency absorbs it (projection
// GREATEST checkpoints, keyed process-manager dispatch). ADR 0049 records the wake-then-drain
// ruling and the sequence-number reasoning.
public sealed class DynamoDbStreamDispatchService : BackgroundService
{
    // This service's own dispatch checkpoint, distinct from every projection checkpoint and from
    // the KurrentDB subscription's. Projection names are the eight constants in ProjectionNames;
    // this shares their table under a name none of them uses.
    public const string CheckpointName = "dynamodb-stream-dispatch";

    private readonly IAmazonDynamoDBStreams _streams;
    private readonly IEventStore _store;
    private readonly ICheckpointStore _checkpointStore;
    private readonly IMessageDispatcher _dispatcher;
    private readonly DynamoDbEventStoreOptions _storeOptions;
    private readonly DynamoDbStreamDispatchOptions _options;
    private readonly ILogger<DynamoDbStreamDispatchService> _logger;

    public DynamoDbStreamDispatchService(
        IAmazonDynamoDBStreams streams,
        IEventStore store,
        ICheckpointStore checkpointStore,
        IMessageDispatcher dispatcher,
        IOptions<DynamoDbEventStoreOptions> storeOptions,
        IOptions<DynamoDbStreamDispatchOptions> options,
        ILogger<DynamoDbStreamDispatchService> logger)
    {
        ArgumentNullException.ThrowIfNull(streams);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(checkpointStore);
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(storeOptions);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        _streams = streams;
        _store = store;
        _checkpointStore = checkpointStore;
        _dispatcher = dispatcher;
        _storeOptions = storeOptions.Value;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Reconnect-on-fault, mirroring the KurrentDB subscription and the outbox processors. The
        // walk runs until it faults or the host stops; a fault logs, backs off, and starts over.
        // Starting over is cheap and safe because the checkpoint is the only state that survives:
        // the next pass re-acquires every iterator and drains behind them.
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex, "DynamoDB stream dispatch faulted; restarting from the stored checkpoint");
                try
                {
                    await Task.Delay(_options.ReconnectBackoff, stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    return;
                }
            }
        }
    }

    private async Task RunAsync(CancellationToken ct)
    {
        var streamArn = await ResolveStreamArnAsync(ct);
        var shards = new ShardWalk();

        // Iterators before the drain, closing the window the header names. Acquiring returns true
        // because every new iterator owes a drain, and the first pass owes the catch-up drain
        // regardless: everything written while this process was down sits below every LATEST point
        // the walk just took.
        await AcquireNewShardIteratorsAsync(streamArn, shards, ct);
        await DrainAsync(ct);

        var sinceRefresh = Stopwatch.StartNew();

        while (!ct.IsCancellationRequested)
        {
            // Discovery off the hot path: only when the interval elapses, or when the walk lost a
            // shard and its successors are the only way forward.
            var acquired = false;
            if (shards.NeedsDiscovery || sinceRefresh.Elapsed >= _options.ShardRefreshInterval)
            {
                acquired = await AcquireNewShardIteratorsAsync(streamArn, shards, ct);
                sinceRefresh.Restart();
            }

            var woken = await ReadAnyRecordsAsync(shards, ct);

            // A new iterator forces a drain even with nothing read: LATEST skipped whatever came
            // before it, and only the drain can find those rows.
            if (woken || acquired)
            {
                await DrainAsync(ct);
            }
            else
            {
                await Task.Delay(_options.EmptyShardBackoff, ct);
            }
        }
    }

    // Reads the log from the checkpoint and dispatches what it finds, until the feed goes quiet.
    // The re-read matters: dispatching a batch takes time, and more events land while it does, so a
    // single pass would leave them for the next wake. The loop ends when a pass dispatches nothing.
    private async Task DrainAsync(CancellationToken ct)
    {
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var checkpoint = await _checkpointStore.GetPositionAsync(CheckpointName, ct);
            var dispatched = 0;

            await foreach (var envelope in _store.ReadAllAsync(checkpoint, ct))
            {
                ct.ThrowIfCancellationRequested();
                await _dispatcher.DispatchAsync(ToOutboxMessage(envelope), ct);

                // Per event, not per batch. A fault mid-batch then redelivers only from the last
                // dispatched event rather than from the batch's start. ReadAllAsync excludes PM
                // rows, so a PM row's position is never checkpointed directly; the next aggregate
                // event's position carries the checkpoint across it, which is what keeps a filtered
                // stretch from pinning the checkpoint below itself forever.
                await _checkpointStore.AdvanceAsync(CheckpointName, envelope.GlobalPosition, ct);
                dispatched++;
            }

            if (dispatched == 0)
            {
                return;
            }
        }
    }

    // The same mapping the KurrentDB subscription makes. OutboxId carries the global position: this
    // engine has no outbox table and therefore no outbox id, and the position is the only durable
    // identity the row has.
    private static OutboxMessage ToOutboxMessage(EventEnvelope envelope)
        => new(
            OutboxId: envelope.GlobalPosition,
            EventId: envelope.EventId,
            EventType: envelope.EventType,
            Event: envelope.Payload,
            Metadata: envelope.Metadata,
            GlobalPosition: envelope.GlobalPosition,
            AttemptCount: 0);

    // The table's stream, found through the Streams client rather than the table client: this
    // service holds no IAmazonDynamoDB, and ListStreams takes a table name.
    private async Task<string> ResolveStreamArnAsync(CancellationToken ct)
    {
        var listed = await _streams.ListStreamsAsync(
            new ListStreamsRequest { TableName = _storeOptions.TableName }, ct);

        foreach (var summary in listed.Streams)
        {
            var described = await _streams.DescribeStreamAsync(
                new DescribeStreamRequest { StreamArn = summary.StreamArn }, ct);
            if (described.StreamDescription.StreamStatus == StreamStatus.ENABLED)
            {
                return summary.StreamArn;
            }
        }

        // A table keeps its disabled streams, so an absent enabled one means the table was
        // provisioned without a stream. That is a composition defect, not a transient fault.
        throw new InvalidOperationException(
            $"Table '{_storeOptions.TableName}' has no enabled stream. " +
            $"DynamoDbTableProvisioner enables one on create; the table predates it or was made elsewhere.");
    }

    // Adds an iterator for every shard the walk does not already hold and has not retired. Returns
    // whether anything was acquired, which the caller turns into a forced drain.
    private async Task<bool> AcquireNewShardIteratorsAsync(
        string streamArn, ShardWalk shards, CancellationToken ct)
    {
        var described = await _streams.DescribeStreamAsync(
            new DescribeStreamRequest { StreamArn = streamArn }, ct);
        shards.NeedsDiscovery = false;
        var acquired = false;

        foreach (var shard in described.StreamDescription.Shards)
        {
            if (!shards.ShouldAcquire(shard.ShardId))
            {
                continue;
            }

            var iterator = await _streams.GetShardIteratorAsync(
                new GetShardIteratorRequest
                {
                    StreamArn = streamArn,
                    ShardId = shard.ShardId,
                    ShardIteratorType = ShardIteratorType.LATEST,
                },
                ct);
            shards.Iterators[shard.ShardId] = iterator.ShardIterator;
            acquired = true;
        }

        return acquired;
    }

    // Walks every live shard once. Returns whether anything arrived, which is the whole question the
    // stream is asked: what a record says is never read.
    private async Task<bool> ReadAnyRecordsAsync(ShardWalk shards, CancellationToken ct)
    {
        var woken = false;

        foreach (var shardId in shards.Iterators.Keys.ToList())
        {
            ct.ThrowIfCancellationRequested();
            GetRecordsResponse page;
            try
            {
                page = await _streams.GetRecordsAsync(
                    new GetRecordsRequest { ShardIterator = shards.Iterators[shardId] }, ct);
            }
            catch (ExpiredIteratorException)
            {
                // The walk fell behind its own iterator's lifetime. Drop it and ask for discovery,
                // which re-acquires from LATEST and forces the drain that covers the gap. Losing an
                // iterator is exactly the case the forced-drain rule exists for.
                shards.Iterators.Remove(shardId);
                shards.NeedsDiscovery = true;
                continue;
            }

            woken |= page.Records.Count > 0;

            if (page.NextShardIterator is null)
            {
                // The shard closed. Retire it so discovery does not hand back an iterator on it
                // every pass, and ask for discovery so its successors are found now rather than at
                // the next refresh.
                shards.Retire(shardId);
                shards.NeedsDiscovery = true;
            }
            else
            {
                shards.Iterators[shardId] = page.NextShardIterator;
            }
        }

        return woken;
    }

    // The walk's shard state, in memory and rebuilt on every restart. Rebuilding costs one
    // re-check per closed shard per restart, which is nothing next to keeping it durable.
    private sealed class ShardWalk
    {
        public Dictionary<string, string> Iterators { get; } = new(StringComparer.Ordinal);

        // Closed shards. DescribeStream keeps listing them for the stream's retention window, so
        // without this the walk re-acquires an iterator on a dead shard every pass forever.
        private HashSet<string> Retired { get; } = new(StringComparer.Ordinal);

        // Set when the walk loses a shard: an expired iterator to replace, or a closed shard whose
        // successors it has not seen. Discovery otherwise waits for the refresh interval.
        public bool NeedsDiscovery { get; set; }

        public bool ShouldAcquire(string shardId)
            => !Iterators.ContainsKey(shardId) && !Retired.Contains(shardId);

        public void Retire(string shardId)
        {
            Iterators.Remove(shardId);
            Retired.Add(shardId);
        }
    }
}
