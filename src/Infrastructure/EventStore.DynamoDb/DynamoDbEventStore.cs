using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.Infrastructure.Versioning;
using Microsoft.Extensions.Options;

namespace EventSourcingCqrs.Infrastructure.EventStore.DynamoDb;

// The DynamoDB event store (PLAN.md Phase 14). One table holds everything: aggregate rows, PM
// rows, the position counter, and the log partition that carries global ordering. DynamoDB has no
// cheap cross-table transaction, and an append has to move all of them at once, so they share a
// table by necessity rather than by preference.
//
// The trade-offs PLAN.md:466 asks to be written down, in present tense, because they are what this
// engine costs today rather than what it might cost later.
//
// The counter is a serialization point, and it is the design's price. ADR 0044 binds every adapter
// to commit-ordered visibility of a single global position. The relational adapters get that from
// an append lock, KurrentDB from its single-writer $all log, and DynamoDB has nothing of the kind:
// no engine-assigned global sequence exists, so this adapter draws positions by reading one
// counter row and conditionally advancing it inside the append's own transaction. Every writer in
// the deployment contends for that row no matter which stream it writes. A spike against the live
// engine measured 12 writers on distinct streams landing 300 appends in 1667 attempts, 82% of them
// cancelled, one writer retrying 42 times, at roughly 19 appends/s. That is not a tuning problem
// to be optimized away later; it is what one global position costs on this engine, and a
// deployment that needs more write throughput than this needs a different ordering guarantee, not
// a bigger table.
//
// The log partition is hot by construction. Every event writes a row under one partition key, so
// the whole deployment's write traffic lands on one DynamoDB partition, which is the shape the
// engine's own guidance tells you to avoid. It is here because the alternative does not work: see
// the GSI note below. Reading the feed is a single Query, which is the compensation.
//
// Every read this adapter issues is a ConsistentRead. The port's readers are projections and
// replays resuming from a checkpoint, and an eventually-consistent read can miss a row below its
// own high-water mark, which would silently break the resume contract (ADR 0044). Consistent reads
// cost twice as much and cannot be served from a GSI, and both of those follow from the same
// choice.
//
// The GSI the plan proposed is dead, and the refutation is the reason the log partition exists.
// PLAN.md:463 proposed a Global Secondary Index for global ordering and replay. DynamoDB rejects
// ConsistentRead against a GSI outright: "Consistent reads are not supported on global secondary
// indexes" (ValidationException), measured against the live engine. A GSI-backed global position
// therefore cannot serve the strongly-consistent ordered read ADR 0044 requires, so ordering rides
// a log partition on the base table, which ConsistentRead does serve. ADR 0049 records the
// refutation, the substrate, and the hot-partition cost it buys.
public sealed class DynamoDbEventStore : IEventStore
{
    private readonly IAmazonDynamoDB _client;
    private readonly EventTypeRegistry _registry;
    private readonly ProcessManagerEventTypeRegistry _pmRegistry;
    private readonly JsonSerializerOptions _jsonOptions;
    private readonly DynamoDbEventStoreOptions _options;

    public DynamoDbEventStore(
        IAmazonDynamoDB client,
        EventTypeRegistry registry,
        ProcessManagerEventTypeRegistry pmRegistry,
        JsonSerializerOptions jsonOptions,
        IOptions<DynamoDbEventStoreOptions> options)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(pmRegistry);
        ArgumentNullException.ThrowIfNull(jsonOptions);
        ArgumentNullException.ThrowIfNull(options);
        _client = client;
        _registry = registry;
        _pmRegistry = pmRegistry;
        _jsonOptions = jsonOptions;
        _options = options.Value;
    }

    public Task AppendAsync(
        StreamId streamId,
        int expectedVersion,
        IReadOnlyList<EventEnvelope> events,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(streamId);
        ArgumentNullException.ThrowIfNull(events);

        var rows = events
            .Select(e => new AppendRow(
                e.StreamId, e.StreamVersion, e.EventId, e.EventType, e.EventVersion,
                _registry.NameFor(e.Payload.GetType()), e.Payload, e.Metadata, e.OccurredUtc))
            .ToList();

        return AppendRowsAsync(streamId, expectedVersion, rows, ct);
    }

    public Task AppendProcessManagerEventsAsync(
        StreamId streamId,
        int expectedVersion,
        IReadOnlyList<ProcessManagerEventEnvelope> events,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(streamId);
        ArgumentNullException.ThrowIfNull(events);

        // PM events land in the same table and draw from the same counter as aggregate events, so
        // one global sequence spans both append paths (ADR 0013 keeps them apart at the type level,
        // not at the storage level). They skip no outbox because this engine has none.
        var rows = events
            .Select(e => new AppendRow(
                e.StreamId, e.StreamVersion, e.EventId, e.EventType, e.EventVersion,
                _pmRegistry.NameFor(e.Payload.GetType()), e.Payload, e.Metadata, e.OccurredUtc))
            .ToList();

        return AppendRowsAsync(streamId, expectedVersion, rows, ct);
    }

    private async Task AppendRowsAsync(
        StreamId streamId, int expectedVersion, IReadOnlyList<AppendRow> rows, CancellationToken ct)
    {
        if (rows.Count == 0)
        {
            return;
        }

        GuardItemBudget(rows.Count);

        // The cap makes a writer that keeps losing the counter race fail loudly rather than spin
        // forever. Contention is this engine's normal case, so the default is generous.
        var cap = _options.MaxAppendAttempts;
        TransactionCanceledException? lastContention = null;
        for (var attempt = 1; attempt <= cap; attempt++)
        {
            ct.ThrowIfCancellationRequested();

            var counter = await ReadCounterAsync(ct);
            var items = BuildTransactionItems(counter, rows);

            try
            {
                await _client.TransactWriteItemsAsync(
                    new TransactWriteItemsRequest { TransactItems = items }, ct);
                return;
            }
            catch (TransactionCanceledException cancelled)
            {
                // Terminal failures throw from here; contention falls through to the retry.
                TranslateCancellation(cancelled, streamId, expectedVersion, rows.Count);
                lastContention = cancelled;
                await Task.Delay(BackoffFor(attempt), ct);
            }
        }

        throw new DynamoDbPositionContentionException(cap, lastContention!);
    }

    private static void GuardItemBudget(int eventCount)
    {
        var itemCount = 1 + (eventCount * DynamoDbSchema.ItemsPerEvent);
        if (itemCount > DynamoDbSchema.TransactionItemLimit)
        {
            throw new DynamoDbAppendTooLargeException(
                eventCount, itemCount, DynamoDbSchema.TransactionItemLimit);
        }
    }

    // Reasons come back positionally, one per transaction item, against the layout
    // DynamoDbSchema documents. Returns for contention, throws for anything terminal.
    private static void TranslateCancellation(
        TransactionCanceledException cancelled,
        StreamId streamId,
        int expectedVersion,
        int eventCount)
    {
        var reasons = cancelled.CancellationReasons;
        var failed = Enumerable.Range(0, reasons.Count)
            .Where(i => reasons[i].Code is not null and not "None")
            .ToList();

        // Order matters. A reused event id must never surface as the concurrency contract, because
        // a handler catching ConcurrencyException would retry a bug forever, so the id row is
        // checked first and its failure propagates untranslated.
        if (failed.Any(i => DynamoDbSchema.IsEventIdRowIndex(i, eventCount)))
        {
            throw cancelled;
        }

        // A taken version is terminal: retrying re-reads the counter and fails the same way.
        if (failed.Any(i => DynamoDbSchema.IsEventRowIndex(i, eventCount)))
        {
            throw new ConcurrencyException(streamId, expectedVersion);
        }

        // Counter or log row only: another writer took the position between the read and the
        // write. Both fail together, because the log row keys on the position the counter handed
        // out. Re-read and retry.
        var contention = failed.Any(i =>
            i == DynamoDbSchema.CounterIndex || DynamoDbSchema.IsLogRowIndex(i, eventCount));
        if (contention)
        {
            return;
        }

        throw cancelled;
    }

    // Small and jittered. The jitter matters more than the curve: without it, writers that
    // collided once collide again on the same schedule.
    private static TimeSpan BackoffFor(int attempt)
        => TimeSpan.FromMilliseconds(Math.Min(2 * attempt, 25) + Random.Shared.Next(0, 15));

    private async Task<long> ReadCounterAsync(CancellationToken ct)
    {
        var read = await _client.GetItemAsync(
            new GetItemRequest
            {
                TableName = _options.TableName,
                Key = CounterKey(),
                ConsistentRead = true,
            },
            ct);

        if (read.Item is not { Count: > 0 })
        {
            throw new InvalidOperationException(
                $"The position counter row is missing from table '{_options.TableName}'. " +
                $"DynamoDbTableProvisioner seeds it; the table was not provisioned.");
        }

        return long.Parse(
            read.Item[DynamoDbSchema.CounterValueAttribute].N, CultureInfo.InvariantCulture);
    }

    // The layout here is the invariant DynamoDbSchema documents and TranslateCancellation reads.
    private List<TransactWriteItem> BuildTransactionItems(long counter, IReadOnlyList<AppendRow> rows)
    {
        var items = new List<TransactWriteItem>(1 + (rows.Count * DynamoDbSchema.ItemsPerEvent))
        {
            new()
            {
                Update = new Update
                {
                    TableName = _options.TableName,
                    Key = CounterKey(),
                    UpdateExpression = "SET #c = :next",
                    ConditionExpression = "#c = :current",
                    ExpressionAttributeNames = new() { ["#c"] = DynamoDbSchema.CounterValueAttribute },
                    ExpressionAttributeValues = new()
                    {
                        [":current"] = Number(counter),
                        [":next"] = Number(counter + rows.Count),
                    },
                },
            },
        };

        items.AddRange(rows.Select((row, i) => new TransactWriteItem
        {
            Put = new Put
            {
                TableName = _options.TableName,
                Item = EventRow(row, counter + 1 + i),
                ConditionExpression = $"attribute_not_exists({DynamoDbSchema.SortKeyAttribute})",
            },
        }));

        items.AddRange(rows.Select((row, i) => new TransactWriteItem
        {
            Put = new Put
            {
                TableName = _options.TableName,
                Item = LogRow(row, counter + 1 + i),
                ConditionExpression = $"attribute_not_exists({DynamoDbSchema.SortKeyAttribute})",
            },
        }));

        items.AddRange(rows.Select(row => new TransactWriteItem
        {
            Put = new Put
            {
                TableName = _options.TableName,
                Item = new()
                {
                    [DynamoDbSchema.PartitionKeyAttribute] =
                        new AttributeValue { S = DynamoDbSchema.EventIdPartitionKeyPrefix + row.EventId },
                    [DynamoDbSchema.SortKeyAttribute] = Number(0),
                },
                ConditionExpression = $"attribute_not_exists({DynamoDbSchema.PartitionKeyAttribute})",
            },
        }));

        return items;
    }

    private Dictionary<string, AttributeValue> EventRow(AppendRow row, long position)
    {
        var item = Envelope(row, position);
        item[DynamoDbSchema.PartitionKeyAttribute] = new AttributeValue { S = row.StreamId.Value };
        item[DynamoDbSchema.SortKeyAttribute] = Number(row.Version);
        return item;
    }

    // The log row carries the whole envelope, not a pointer to it. ReadAllAsync is then one Query
    // on one partition rather than a fan-out of per-stream reads, which is what makes the hot
    // partition worth its cost. The duplication is the trade.
    private Dictionary<string, AttributeValue> LogRow(AppendRow row, long position)
    {
        var item = Envelope(row, position);
        item[DynamoDbSchema.PartitionKeyAttribute] =
            new AttributeValue { S = DynamoDbSchema.LogPartitionKey };
        item[DynamoDbSchema.SortKeyAttribute] = Number(position);
        return item;
    }

    private Dictionary<string, AttributeValue> Envelope(AppendRow row, long position)
        => new()
        {
            [DynamoDbSchema.StreamIdAttribute] = new AttributeValue { S = row.StreamId.Value },
            [DynamoDbSchema.VersionAttribute] = Number(row.Version),
            [DynamoDbSchema.EventIdAttribute] = new AttributeValue { S = row.EventId.ToString() },
            [DynamoDbSchema.EventTypeAttribute] = new AttributeValue { S = row.EventType },
            [DynamoDbSchema.EventVersionAttribute] = Number(row.EventVersion),
            [DynamoDbSchema.PayloadAttribute] =
                new AttributeValue { S = JsonSerializer.Serialize(row.Payload, row.Payload.GetType(), _jsonOptions) },
            [DynamoDbSchema.MetadataAttribute] =
                new AttributeValue { S = JsonSerializer.Serialize(row.Metadata, _jsonOptions) },
            [DynamoDbSchema.OccurredUtcAttribute] =
                new AttributeValue { S = row.OccurredUtc.ToString("O", CultureInfo.InvariantCulture) },
            [DynamoDbSchema.PositionAttribute] = Number(position),
            [DynamoDbSchema.TypeNameAttribute] = new AttributeValue { S = row.TypeName },
            // Lifted out of the metadata JSON so ReadAllForTenantAsync can filter on it
            // server-side. Written from the same metadata value hydration reads back, so the
            // top-level copy and the document copy come from one source and cannot drift.
            [DynamoDbSchema.TenantAttribute] =
                new AttributeValue { S = row.Metadata.Tenant.Value.ToString("N") },
        };

    public async Task<IReadOnlyList<EventEnvelope>> ReadStreamAsync(
        StreamId streamId,
        int fromVersion = 0,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(streamId);

        var rows = await QueryPartitionAsync(
            streamId.Value,
            $"{DynamoDbSchema.PartitionKeyAttribute} = :pk AND {DynamoDbSchema.SortKeyAttribute} > :from",
            new() { [":pk"] = new AttributeValue { S = streamId.Value }, [":from"] = Number(fromVersion) },
            ct);

        return rows.Select(HydrateAggregate).ToList();
    }

    public async Task<IReadOnlyList<ProcessManagerEventEnvelope>> ReadProcessManagerStreamAsync(
        StreamId streamId,
        int fromVersion = 0,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(streamId);

        // The guard is on the shape of the id, not on whether the stream exists: PM payloads
        // resolve through a separate registry (ADR 0013), so a caller reaching for one with an
        // aggregate id has a bug and gets told (ADR 0011). Matching the relational adapters'
        // ArgumentException keeps the shipped stores raising one type, which the contract suite
        // pins loosely on purpose.
        if (!streamId.Value.StartsWith(StreamPrefixes.ProcessManagerPrefix, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "ReadProcessManagerStreamAsync requires a process-manager stream id " +
                $"(pm- prefix); got '{streamId}'.",
                nameof(streamId));
        }

        var rows = await QueryPartitionAsync(
            streamId.Value,
            $"{DynamoDbSchema.PartitionKeyAttribute} = :pk AND {DynamoDbSchema.SortKeyAttribute} > :from",
            new() { [":pk"] = new AttributeValue { S = streamId.Value }, [":from"] = Number(fromVersion) },
            ct);

        return rows.Select(HydrateProcessManager).ToList();
    }

    public async IAsyncEnumerable<EventEnvelope> ReadAllAsync(
        long fromPosition,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var rows = await QueryPartitionAsync(
            DynamoDbSchema.LogPartitionKey,
            $"{DynamoDbSchema.PartitionKeyAttribute} = :pk AND {DynamoDbSchema.SortKeyAttribute} > :from",
            new()
            {
                [":pk"] = new AttributeValue { S = DynamoDbSchema.LogPartitionKey },
                [":from"] = Number(fromPosition),
            },
            ct);

        foreach (var row in rows.Where(IsAggregateRow))
        {
            yield return HydrateAggregate(row);
        }
    }

    public async IAsyncEnumerable<EventEnvelope> ReadAllForTenantAsync(
        TenantId tenant,
        long fromPosition,
        long toPositionInclusive,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        // The tenant predicate runs server-side on the log partition's own Query. It is a
        // FilterExpression rather than a key condition, so DynamoDB still reads every row in the
        // position window and then drops the ones that do not match: the read capacity is the
        // window's, not the tenant's. What it buys is that other tenants' payloads never cross the
        // wire or get deserialized, which is the part that matters on a partition carrying every
        // tenant's traffic. Narrowing the capacity too would need the tenant in a key, and no key
        // on this table can carry it without giving up the single ordered feed.
        var rows = await QueryPartitionAsync(
            DynamoDbSchema.LogPartitionKey,
            $"{DynamoDbSchema.PartitionKeyAttribute} = :pk " +
            $"AND {DynamoDbSchema.SortKeyAttribute} BETWEEN :from AND :to",
            new()
            {
                [":pk"] = new AttributeValue { S = DynamoDbSchema.LogPartitionKey },
                // fromPosition is exclusive and BETWEEN is inclusive on both ends, so the floor
                // moves up one. Positions are integers, so this is exact rather than approximate.
                [":from"] = Number(fromPosition + 1),
                [":to"] = Number(toPositionInclusive),
                [":tenant"] = new AttributeValue { S = tenant.Value.ToString("N") },
            },
            ct,
            filterExpression: $"{DynamoDbSchema.TenantAttribute} = :tenant");

        foreach (var row in rows.Where(IsAggregateRow))
        {
            yield return HydrateAggregate(row);
        }
    }

    // The feed excludes PM streams (ADR 0013): projections derive workflow state from aggregate
    // events and never from PM streams. The log carries both, because the committed-position
    // sequence spans both append paths, so the exclusion is a read-side filter rather than a
    // write-side one.
    private static bool IsAggregateRow(Dictionary<string, AttributeValue> row)
        => !row[DynamoDbSchema.StreamIdAttribute].S
            .StartsWith(StreamPrefixes.ProcessManagerPrefix, StringComparison.Ordinal);

    // A filtered page can come back empty with a continuation key, because the filter runs after
    // the page is read: the loop keys on LastEvaluatedKey and never on the item count.
    private async Task<List<Dictionary<string, AttributeValue>>> QueryPartitionAsync(
        string partitionKey,
        string keyCondition,
        Dictionary<string, AttributeValue> values,
        CancellationToken ct,
        string? filterExpression = null)
    {
        var rows = new List<Dictionary<string, AttributeValue>>();
        Dictionary<string, AttributeValue>? start = null;

        do
        {
            ct.ThrowIfCancellationRequested();
            var page = await _client.QueryAsync(
                new QueryRequest
                {
                    TableName = _options.TableName,
                    KeyConditionExpression = keyCondition,
                    FilterExpression = filterExpression,
                    ExpressionAttributeValues = values,
                    ConsistentRead = true,
                    ScanIndexForward = true,
                    ExclusiveStartKey = start,
                },
                ct);

            rows.AddRange(page.Items);
            start = page.LastEvaluatedKey is { Count: > 0 } ? page.LastEvaluatedKey : null;
        }
        while (start is not null);

        return rows;
    }

    private EventEnvelope HydrateAggregate(Dictionary<string, AttributeValue> row)
    {
        var streamId = StreamId.Parse(row[DynamoDbSchema.StreamIdAttribute].S);
        var typeName = row[DynamoDbSchema.TypeNameAttribute].S;
        Type clrType;
        try
        {
            clrType = _registry.TypeFor(typeName);
        }
        catch (UnknownEventTypeException ex)
        {
            throw new UnknownEventTypeException(typeName, streamId, ex);
        }

        var payload = (IDomainEvent)JsonSerializer.Deserialize(
            row[DynamoDbSchema.PayloadAttribute].S, clrType, _jsonOptions)!;

        return new EventEnvelope(
            StreamId: streamId,
            StreamVersion: int.Parse(row[DynamoDbSchema.VersionAttribute].N, CultureInfo.InvariantCulture),
            EventId: Guid.Parse(row[DynamoDbSchema.EventIdAttribute].S),
            EventType: row[DynamoDbSchema.EventTypeAttribute].S,
            EventVersion: int.Parse(row[DynamoDbSchema.EventVersionAttribute].N, CultureInfo.InvariantCulture),
            Payload: payload,
            Metadata: ReadMetadata(row),
            OccurredUtc: ReadOccurredUtc(row),
            GlobalPosition: long.Parse(row[DynamoDbSchema.PositionAttribute].N, CultureInfo.InvariantCulture));
    }

    private ProcessManagerEventEnvelope HydrateProcessManager(Dictionary<string, AttributeValue> row)
    {
        var streamId = StreamId.Parse(row[DynamoDbSchema.StreamIdAttribute].S);
        var typeName = row[DynamoDbSchema.TypeNameAttribute].S;
        Type clrType;
        try
        {
            clrType = _pmRegistry.TypeFor(typeName);
        }
        catch (UnknownEventTypeException ex)
        {
            throw new UnknownEventTypeException(typeName, streamId, ex);
        }

        var payload = (IProcessManagerEvent)JsonSerializer.Deserialize(
            row[DynamoDbSchema.PayloadAttribute].S, clrType, _jsonOptions)!;

        return new ProcessManagerEventEnvelope(
            StreamId: streamId,
            StreamVersion: int.Parse(row[DynamoDbSchema.VersionAttribute].N, CultureInfo.InvariantCulture),
            EventId: Guid.Parse(row[DynamoDbSchema.EventIdAttribute].S),
            EventType: row[DynamoDbSchema.EventTypeAttribute].S,
            EventVersion: int.Parse(row[DynamoDbSchema.EventVersionAttribute].N, CultureInfo.InvariantCulture),
            Payload: payload,
            Metadata: ReadMetadata(row),
            OccurredUtc: ReadOccurredUtc(row),
            GlobalPosition: long.Parse(row[DynamoDbSchema.PositionAttribute].N, CultureInfo.InvariantCulture));
    }

    // The shared tolerant read (ADR 0048): a pre-tenancy row carries no tenant, and the coalesce
    // maps it to the default rather than handing a null tenant downstream.
    private EventMetadata ReadMetadata(Dictionary<string, AttributeValue> row)
        => EventMetadataReader.Read(row[DynamoDbSchema.MetadataAttribute].S, _jsonOptions);

    private static DateTime ReadOccurredUtc(Dictionary<string, AttributeValue> row)
        => DateTime.Parse(
            row[DynamoDbSchema.OccurredUtcAttribute].S,
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind);

    private static AttributeValue Number(long value)
        => new() { N = value.ToString(CultureInfo.InvariantCulture) };

    private Dictionary<string, AttributeValue> CounterKey()
        => new()
        {
            [DynamoDbSchema.PartitionKeyAttribute] =
                new AttributeValue { S = DynamoDbSchema.CounterPartitionKey },
            [DynamoDbSchema.SortKeyAttribute] = Number(0),
        };

    // One append's worth of one event, flattened out of whichever envelope type it arrived in, so
    // the two append paths share the transaction builder rather than duplicating it.
    private sealed record AppendRow(
        StreamId StreamId,
        int Version,
        Guid EventId,
        string EventType,
        int EventVersion,
        string TypeName,
        object Payload,
        EventMetadata Metadata,
        DateTime OccurredUtc);
}
