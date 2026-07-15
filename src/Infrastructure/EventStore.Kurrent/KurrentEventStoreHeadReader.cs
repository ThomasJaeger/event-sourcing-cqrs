using EventSourcingCqrs.Domain.Abstractions;
using KurrentDB.Client;

namespace EventSourcingCqrs.Infrastructure.EventStore.Kurrent;

// The head of the KurrentDB $all feed for operational lag reporting, the engine-specific counterpart to
// PostgresEventStoreHeadReader. It reads the last committed aggregate-feed position by reading $all
// backwards from its end with the same server-side filter the store's ReadAllAsync uses, so the head it
// reports lives in the same commit-position space as the projection checkpoints the lag reader subtracts
// it from. An empty aggregate feed maps to 0, matching the Postgres reader's COALESCE-to-0 contract.
public sealed class KurrentEventStoreHeadReader : IEventStoreHeadPosition
{
    private readonly KurrentDBClient _client;

    public KurrentEventStoreHeadReader(KurrentDBClient client)
    {
        ArgumentNullException.ThrowIfNull(client);
        _client = client;
    }

    public async Task<long> GetHeadPositionAsync(CancellationToken ct)
    {
        // Read $all backwards from its end, one event, through the aggregate-feed filter. An empty
        // aggregate feed yields no event and maps to 0, the same answer PostgresEventStoreHeadReader gives
        // an empty events table through COALESCE(MAX(global_position), 0). A raw backwards read would
        // instead return a KurrentDB system event, so the filter is load-bearing here.
        //
        // The filter excludes PM streams as well as system streams, so the head is aggregate-only and
        // lands in the projection checkpoints' space. The Postgres reader's MAX(global_position) spans the
        // aggregate and PM rows the shared events table holds, so a PM-tailed log reports a different head
        // across the two engines. This aggregate-filtered head divergence is recorded in ADR 0047.
        var read = _client.ReadAllAsync(
            Direction.Backwards,
            Position.End,
            StreamFilter.RegularExpression(KurrentEventStore.AggregateFeedStreamFilter),
            maxCount: 1,
            cancellationToken: ct);

        await foreach (var resolved in read.WithCancellation(ct).ConfigureAwait(false))
        {
            return checked((long)resolved.Event.Position.CommitPosition);
        }
        return 0;
    }
}
