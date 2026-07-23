using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.Domain.Sales;
using Npgsql;

namespace EventSourcingCqrs.Migration.Demo.Cdc;

// Chapter 18: the CDC reader. It reads pending rows from legacy.legacy_changes past its checkpoint,
// translates each to domain events, appends them to the event store keyed by the order's stream
// through the shared EventStreamAppender, and then advances the checkpoint. It is at-least-once: the
// checkpoint advances only after the appends commit, so a crash between the two replays the batch. The
// append side does not dedupe, so a replay re-emits, which the demo accepts as the cost of the simpler
// cursor.
public sealed class CdcReader
{
    // Lands in EventMetadata.Source so a stored event names the CDC reader as its writer.
    private const string Source = "migration-cdc";

    private readonly string _legacyConnectionString;
    private readonly IEventStore _eventStore;
    private readonly ICurrentEventSchemaVersions _schemaVersions;

    public CdcReader(
        string legacyConnectionString,
        IEventStore eventStore,
        ICurrentEventSchemaVersions schemaVersions)
    {
        _legacyConnectionString = legacyConnectionString;
        _eventStore = eventStore;
        _schemaVersions = schemaVersions;
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(_legacyConnectionString);
        await connection.OpenAsync(cancellationToken);

        var checkpoint = await ReadCheckpointAsync(connection, cancellationToken);
        var changes = await ReadPendingChangesAsync(connection, checkpoint, cancellationToken);
        if (changes.Count == 0)
        {
            return;
        }

        var appender = new EventStreamAppender(_eventStore, _schemaVersions);
        foreach (var group in Translate(changes).GroupBy(e => e.OrderId))
        {
            var streamId = StreamId.ForAggregate<Order>(
                WellKnownTenants.Default, LegacyChangeTranslator.OrderIdFor(group.Key));
            var events = group
                .Select(p => new AppendedEvent(p.Event, p.CorrelationId, p.OccurredUtc))
                .ToList();
            await appender.AppendAsync(
                streamId, events, Source, LegacyChangeTranslator.SystemActorId, cancellationToken);
        }

        // After the appends, not before: a crash here replays the batch rather than skipping it.
        await AdvanceCheckpointAsync(connection, changes[^1].ChangeId, cancellationToken);
    }

    private IReadOnlyList<PendingEvent> Translate(IReadOnlyList<LegacyChange> changes)
    {
        var translator = new LegacyChangeTranslator();
        var pending = new List<PendingEvent>();
        foreach (var change in changes)
        {
            var events = translator.Translate(change.Operation, change.PayloadJson, change.OccurredUtc);
            if (events.Count == 0)
            {
                Console.WriteLine(
                    $"CDC: change {change.ChangeId} ('{change.Operation}') maps to no domain event; skipped.");
                continue;
            }

            // Fresh correlation per legacy record, shared by the events that record produces.
            var correlationId = Guid.NewGuid();
            foreach (var @event in events)
            {
                pending.Add(new PendingEvent(change.RecordId, @event, correlationId, change.OccurredUtc));
            }
        }

        return pending;
    }

    private static async Task<long> ReadCheckpointAsync(NpgsqlConnection connection, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT last_change_id FROM legacy.cdc_checkpoint WHERE id = 1";
        return (long)(await command.ExecuteScalarAsync(ct))!;
    }

    private static async Task<IReadOnlyList<LegacyChange>> ReadPendingChangesAsync(
        NpgsqlConnection connection, long checkpoint, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT change_id, operation, record_id, payload, occurred_utc "
            + "FROM legacy.legacy_changes WHERE change_id > @checkpoint ORDER BY change_id";
        command.Parameters.AddWithValue("checkpoint", checkpoint);

        var changes = new List<LegacyChange>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            changes.Add(new LegacyChange(
                reader.GetInt64(0),
                reader.GetString(1)[0],
                reader.GetInt64(2),
                reader.GetString(3),
                DateTime.SpecifyKind(reader.GetDateTime(4), DateTimeKind.Utc)));
        }

        return changes;
    }

    private static async Task AdvanceCheckpointAsync(
        NpgsqlConnection connection, long lastChangeId, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE legacy.cdc_checkpoint SET last_change_id = @last WHERE id = 1";
        command.Parameters.AddWithValue("last", lastChangeId);
        await command.ExecuteNonQueryAsync(ct);
    }

    private sealed record LegacyChange(
        long ChangeId, char Operation, long RecordId, string PayloadJson, DateTime OccurredUtc);

    private sealed record PendingEvent(
        long OrderId, IDomainEvent Event, Guid CorrelationId, DateTime OccurredUtc);
}
