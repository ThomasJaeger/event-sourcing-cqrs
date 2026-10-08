using EventSourcingCqrs.Domain.Abstractions;
using NpgsqlTypes;

namespace EventSourcingCqrs.Infrastructure.EventStore.Postgres;

// Operator discovery reads indexed IDs and metadata columns, never event payloads.
// The page is chosen before summaries so counts do not group the entire event log.
public sealed class PostgresEventCatalogReader : IEventCatalogReader
{
    private readonly INpgsqlConnectionFactory _factory;

    public PostgresEventCatalogReader(INpgsqlConnectionFactory factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        _factory = factory;
    }

    public async Task<StreamCatalogPage> ListStreamsAsync(
        string? afterStreamId, int pageSize, CancellationToken ct)
    {
        ValidatePageSize(pageSize);
        if (afterStreamId is not null)
        {
            _ = StreamId.Parse(afterStreamId);
        }
        ct.ThrowIfCancellationRequested();

        await using var connection = await _factory.OpenConnectionAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            WITH selected_ids AS MATERIALIZED (
                SELECT DISTINCT stream_id
                FROM event_store.events
                WHERE stream_id > @after AND stream_id NOT LIKE 'pm-%'
                ORDER BY stream_id
                LIMIT @limit
            )
            SELECT ids.stream_id, summary.event_count, summary.last_occurred_utc, summary.tenant_count
            FROM selected_ids AS ids
            CROSS JOIN LATERAL (
                SELECT COUNT(*) AS event_count, MAX(occurred_utc) AS last_occurred_utc,
                       COUNT(DISTINCT tenant_id)::integer AS tenant_count
                FROM event_store.events AS events
                WHERE events.stream_id = ids.stream_id
            ) AS summary
            ORDER BY ids.stream_id
            """;
        // Empty text sorts before every valid stream ID; subsequent cursors are validated IDs.
        command.Parameters.AddWithValue("after", NpgsqlDbType.Text, afterStreamId ?? string.Empty);
        command.Parameters.AddWithValue("limit", NpgsqlDbType.Integer, pageSize + 1);

        var items = new List<StreamCatalogEntry>(pageSize);
        var hasMore = false;
        await using var rows = await command.ExecuteReaderAsync(ct);
        while (await rows.ReadAsync(ct))
        {
            if (items.Count == pageSize)
            {
                hasMore = true;
                break;
            }
            items.Add(new StreamCatalogEntry(
                rows.GetString(0), rows.GetInt64(1),
                DateTime.SpecifyKind(rows.GetDateTime(2), DateTimeKind.Utc), rows.GetInt32(3)));
        }
        return new StreamCatalogPage(items, hasMore);
    }

    public async Task<CorrelationCatalogPage> ListCorrelationsAsync(
        Guid? afterCorrelationId, int pageSize, CancellationToken ct)
    {
        ValidatePageSize(pageSize);
        if (afterCorrelationId == Guid.Empty)
        {
            throw new ArgumentException("A correlation cursor must be a non-empty ID.", nameof(afterCorrelationId));
        }
        ct.ThrowIfCancellationRequested();

        await using var connection = await _factory.OpenConnectionAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            WITH selected_ids AS MATERIALIZED (
                SELECT DISTINCT correlation_id
                FROM event_store.events
                WHERE correlation_id IS NOT NULL AND correlation_id > @after
                ORDER BY correlation_id
                LIMIT @limit
            )
            SELECT ids.correlation_id, summary.event_count, summary.stream_count,
                   summary.tenant_count, summary.last_occurred_utc
            FROM selected_ids AS ids
            CROSS JOIN LATERAL (
                SELECT COUNT(*) AS event_count, COUNT(DISTINCT stream_id)::integer AS stream_count,
                       COUNT(DISTINCT tenant_id)::integer AS tenant_count,
                       MAX(occurred_utc) AS last_occurred_utc
                FROM event_store.events AS events
                WHERE events.correlation_id = ids.correlation_id
            ) AS summary
            ORDER BY ids.correlation_id
            """;
        // PostgreSQL's UUID order puts the empty sentinel first. The exclusive lower bound
        // excludes it on the first page and uses the same ordering on every later page.
        command.Parameters.AddWithValue("after", NpgsqlDbType.Uuid, afterCorrelationId ?? Guid.Empty);
        command.Parameters.AddWithValue("limit", NpgsqlDbType.Integer, pageSize + 1);

        var items = new List<CorrelationCatalogEntry>(pageSize);
        var hasMore = false;
        await using var rows = await command.ExecuteReaderAsync(ct);
        while (await rows.ReadAsync(ct))
        {
            if (items.Count == pageSize)
            {
                hasMore = true;
                break;
            }
            items.Add(new CorrelationCatalogEntry(
                rows.GetGuid(0), rows.GetInt64(1), rows.GetInt32(2), rows.GetInt32(3),
                DateTime.SpecifyKind(rows.GetDateTime(4), DateTimeKind.Utc)));
        }
        return new CorrelationCatalogPage(items, hasMore);
    }

    private static void ValidatePageSize(int pageSize)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(pageSize, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(pageSize, 100);
    }
}
