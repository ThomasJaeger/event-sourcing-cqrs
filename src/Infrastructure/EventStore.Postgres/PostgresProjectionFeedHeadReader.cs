using EventSourcingCqrs.Domain.Abstractions;

namespace EventSourcingCqrs.Infrastructure.EventStore.Postgres;

// Reads the head of the projection feed, the counterpart to PostgresEventStoreHeadReader for
// callers needing a reachable target rather than the log's true tail. Same connection factory, same
// COALESCE to 0 on an empty result, and the same read-only single statement opening no transaction.
//
// The predicate is the one ReadAllAsync applies, so the position this returns is one the feed will
// hand a projection. Taking MAX under that predicate rather than paging backwards keeps it a single
// index-assisted read: global_position is the events table's primary key, so the planner walks the
// index down from the end and stops at the first row the filter admits.
//
// A recorded risk, not a problem this file solves. The exclusion below is the fifth literal of its
// kind in the tree, after the two in PostgresEventStore and the two in SqlServerEventStore, and
// nothing couples the five. No test asserts any of their text, no generator emits them, no analyzer
// checks them, and only one of the other four has a behavioural fact behind it. They all derive by
// hand from StreamPrefixes.ProcessManagerPrefix, which StreamPrefixesTests pins to its value while
// reading no SQL at all. A sixth literal, or a typo in this one, would go uncaught by everything
// except a fact that exercises the behaviour.
public sealed class PostgresProjectionFeedHeadReader : IProjectionFeedHeadPosition
{
    private readonly INpgsqlConnectionFactory _factory;

    public PostgresProjectionFeedHeadReader(INpgsqlConnectionFactory factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        _factory = factory;
    }

    public async Task<long> GetFeedHeadPositionAsync(CancellationToken ct)
    {
        await using var connection = await _factory.OpenConnectionAsync(ct);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText =
            "SELECT COALESCE(MAX(global_position), 0) FROM event_store.events " +
            "WHERE stream_id NOT LIKE 'pm-%'";
        return (long)(await cmd.ExecuteScalarAsync(ct))!;
    }
}
