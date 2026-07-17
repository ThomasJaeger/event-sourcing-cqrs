using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using EventSourcingCqrs.Domain.Abstractions;
using Microsoft.Extensions.Options;

namespace EventSourcingCqrs.Infrastructure.EventStore.DynamoDb;

// The head of the DynamoDB log partition for operational lag reporting, the engine-specific
// counterpart to PostgresEventStoreHeadReader and KurrentEventStoreHeadReader. The AdminConsole's
// projection-lag read subtracts each projection's checkpoint from it.
//
// One Query, backwards, one row. The log partition's sort key is the global position, so its last
// row is the head and DynamoDB can find it without reading the partition: ScanIndexForward false
// starts at the high end and Limit 1 stops there. ConsistentRead for the reason every read on this
// adapter is consistent (ADR 0044): a lag number computed from a stale head reports a projection
// ahead of the log it is following. An empty log yields no row and maps to 0, the contract the port
// states and the same answer PostgresEventStoreHeadReader gives through COALESCE to 0. The counter
// row lives under its own partition key and is never in this Query's way.
//
// THE HEAD IS UNFILTERED, AND THAT IS A CHOICE WITH A COST. The log partition carries process
// manager rows as well as aggregate rows: both append paths draw from one counter and write one log
// row per event, so the exclusion elsewhere in this adapter is a read-side filter rather than a
// write-side one (see IsAggregateRow, which ReadAllAsync applies and this does not). So this head is
// the tail of everything committed, PM events included, and on a PM-tailed log it reports a position
// no projection can reach: projections checkpoint at positions drawn from ReadAllAsync, which skips
// PM rows, so a fully caught-up projection shows a small permanent lag until the next aggregate
// append. That is the PostgreSQL reader's characteristic exactly, where MAX(global_position) spans
// the shared events table, and it is the opposite of KurrentDB's, whose head reads $all through the
// aggregate-feed filter and lands in the checkpoints' own space (ADR 0047).
//
// A filtered tail was considered and rejected on substrate cost. There is no key that separates the
// two families: the log partition's sort key is the position and nothing else, so excluding PM rows
// means a FilterExpression, which DynamoDB applies after reading. A filtered backwards Query with
// Limit 1 returns an empty page whenever the last row is a PM row, and the reader would have to page
// backwards through the log until it found an aggregate row. On a PM-heavy tail that walks an
// unbounded number of rows, at the log partition's own read capacity, every time an operator loads
// the dashboard. Kurrent gets its filter free because the engine applies it server-side before
// paging; this engine would charge for it. A cheap head that overstates lag by the PM tail beats an
// unbounded scan that states it exactly. ADR 0049 records the mapping and this trade.
public sealed class DynamoDbEventStoreHeadReader : IEventStoreHeadPosition
{
    private readonly IAmazonDynamoDB _client;
    private readonly DynamoDbEventStoreOptions _options;

    public DynamoDbEventStoreHeadReader(
        IAmazonDynamoDB client, IOptions<DynamoDbEventStoreOptions> options)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(options);
        _client = client;
        _options = options.Value;
    }

    public async Task<long> GetHeadPositionAsync(CancellationToken ct)
    {
        var response = await _client.QueryAsync(
            new QueryRequest
            {
                TableName = _options.TableName,
                KeyConditionExpression = $"{DynamoDbSchema.PartitionKeyAttribute} = :pk",
                ExpressionAttributeValues = new()
                {
                    [":pk"] = new AttributeValue { S = DynamoDbSchema.LogPartitionKey },
                },
                ConsistentRead = true,
                ScanIndexForward = false,
                Limit = 1,
            },
            ct);

        if (response.Items.Count == 0)
        {
            return 0;
        }

        // The sort key is the position, so the row's key is the answer and the position attribute it
        // also carries is not read. They are written from one value, so this is a choice of which
        // copy to trust rather than a risk of disagreeing with the other.
        return long.Parse(
            response.Items[0][DynamoDbSchema.SortKeyAttribute].N,
            System.Globalization.CultureInfo.InvariantCulture);
    }
}
