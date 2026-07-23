using System.Text.Json;
using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.Domain.Sales;
using EventSourcingCqrs.Infrastructure.Versioning;
using EventSourcingCqrs.Migration.Demo.Cdc;
using Npgsql;

namespace EventSourcingCqrs.Migration.Demo.LegacyOutbox;

// Chapter 18: the outbox-on-legacy drain side. It reads unemitted rows from legacy.legacy_outbox,
// deserializes each back into its domain event through the shared registry and JSON options, appends
// them to the event store through the shared EventStreamAppender, and marks the rows emitted. It is
// at-least-once: a row is stamped emitted only after its append commits, so a crash between the two
// redrains and re-appends. Happy-path redrain idempotency comes from the emitted_utc predicate.
public sealed class LegacyOutboxEmitter
{
    // Lands in EventMetadata.Source so a stored event names the outbox emitter as its writer.
    private const string Source = "migration-outbox";

    private readonly string _legacyConnectionString;
    private readonly IEventStore _eventStore;
    private readonly ICurrentEventSchemaVersions _schemaVersions;
    private readonly EventTypeRegistry _eventTypes;
    private readonly JsonSerializerOptions _jsonOptions;

    public LegacyOutboxEmitter(
        string legacyConnectionString,
        IEventStore eventStore,
        ICurrentEventSchemaVersions schemaVersions,
        EventTypeRegistry eventTypes,
        JsonSerializerOptions jsonOptions)
    {
        _legacyConnectionString = legacyConnectionString;
        _eventStore = eventStore;
        _schemaVersions = schemaVersions;
        _eventTypes = eventTypes;
        _jsonOptions = jsonOptions;
    }

    public async Task DrainAsync(CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(_legacyConnectionString);
        await connection.OpenAsync(cancellationToken);

        var rows = await ReadUnemittedAsync(connection, cancellationToken);
        if (rows.Count == 0)
        {
            return;
        }

        var appender = new EventStreamAppender(_eventStore, _schemaVersions);
        foreach (var group in rows.GroupBy(r => r.AggregateId))
        {
            var streamId = StreamId.ForAggregate<Order>(WellKnownTenants.Default, group.Key);
            var events = group.Select(ToAppendedEvent).ToList();
            await appender.AppendAsync(
                streamId, events, Source, LegacyChangeTranslator.SystemActorId, cancellationToken);
        }

        // After the appends, not before: a crash here redrains and re-appends rather than dropping.
        await MarkEmittedAsync(connection, rows.Select(r => r.OutboxId).ToList(), cancellationToken);
    }

    private AppendedEvent ToAppendedEvent(OutboxRow row)
    {
        var clrType = _eventTypes.TypeFor(row.TypeName);
        var @event = (IDomainEvent)JsonSerializer.Deserialize(row.Payload, clrType, _jsonOptions)!;
        // Fresh correlation per outbox row.
        return new AppendedEvent(@event, Guid.NewGuid(), row.OccurredUtc);
    }

    private static async Task<IReadOnlyList<OutboxRow>> ReadUnemittedAsync(
        NpgsqlConnection connection, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT outbox_id, aggregate_id, type_name, payload, occurred_utc "
            + "FROM legacy.legacy_outbox WHERE emitted_utc IS NULL ORDER BY outbox_id";

        var rows = new List<OutboxRow>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            rows.Add(new OutboxRow(
                reader.GetInt64(0),
                reader.GetGuid(1),
                reader.GetString(2),
                reader.GetFieldValue<string>(3),
                DateTime.SpecifyKind(reader.GetDateTime(4), DateTimeKind.Utc)));
        }

        return rows;
    }

    private static async Task MarkEmittedAsync(
        NpgsqlConnection connection, IReadOnlyList<long> outboxIds, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            "UPDATE legacy.legacy_outbox SET emitted_utc = now() WHERE outbox_id = ANY(@ids)";
        command.Parameters.AddWithValue("ids", outboxIds.ToArray());
        await command.ExecuteNonQueryAsync(ct);
    }

    private sealed record OutboxRow(
        long OutboxId, Guid AggregateId, string TypeName, string Payload, DateTime OccurredUtc);
}
