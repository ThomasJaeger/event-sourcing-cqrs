using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Microsoft.Extensions.Options;

namespace EventSourcingCqrs.Infrastructure.EventStore.DynamoDb;

// Creates the events table if it is absent, and waits for it to serve reads. This is the
// DynamoDB counterpart to the relational migration runners, and it is a first-class type for the
// same reason they are: the schema is a deployment concern the tests and the Workers host both
// need, not a side effect of composing a store.
//
// The schema is the whole design, so it is worth stating plainly. One partition key (S), one
// sort key (N), and nothing else:
//
//   pk = the stream id for an aggregate row and for a PM row alike, which StreamId composes as
//        "{prefix}:{id:N}", or "{prefix}:{tenant:N}:{id:N}" outside the default tenant. The
//        prefix is the lowercased aggregate type name, or the "pm-" form for a process manager.
//        Also a reserved literal for the position counter, a reserved literal for the log
//        partition that carries global ordering, and an "$eid#" prefix for event-id dedupe rows.
//   sk = the stream version for an event row, the position for a log row.
//
// No Global Secondary Index. PLAN.md's Phase 14 GSI goal proposed a GSI for global ordering and replay, and a
// spike against the live engine refuted it: DynamoDB rejects ConsistentRead against a GSI with
// "Consistent reads are not supported on global secondary indexes" (ValidationException). A
// GSI-backed global position cannot serve the strongly-consistent ordered read ADR 0044's
// commit-order visibility requires, so global ordering rides a log partition on the base table,
// which ConsistentRead does serve. ADR 0049 records the refutation and the substrate choice.
public sealed class DynamoDbTableProvisioner
{
    private readonly IAmazonDynamoDB _client;
    private readonly DynamoDbEventStoreOptions _options;

    public DynamoDbTableProvisioner(
        IAmazonDynamoDB client, IOptions<DynamoDbEventStoreOptions> options)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(options);
        _client = client;
        _options = options.Value;
    }

    public async Task EnsureTableAsync(CancellationToken ct)
    {
        if (!await TableExistsAsync(ct))
        {
            try
            {
                await _client.CreateTableAsync(
                    new CreateTableRequest
                    {
                        TableName = _options.TableName,
                        AttributeDefinitions =
                        [
                            new AttributeDefinition(
                                DynamoDbSchema.PartitionKeyAttribute, ScalarAttributeType.S),
                            new AttributeDefinition(
                                DynamoDbSchema.SortKeyAttribute, ScalarAttributeType.N),
                        ],
                        KeySchema =
                        [
                            new KeySchemaElement(DynamoDbSchema.PartitionKeyAttribute, KeyType.HASH),
                            new KeySchemaElement(DynamoDbSchema.SortKeyAttribute, KeyType.RANGE),
                        ],
                        BillingMode = BillingMode.PAY_PER_REQUEST,
                        // The change feed the dispatch service wakes on (PLAN.md's Phase 14 Streams goal). KEYS_ONLY is
                        // everything this system reads: the dispatcher treats a record as a wake
                        // signal and never parses it, then reads the envelope from the log
                        // partition, so an image would be carried across the wire and dropped.
                        // A consumer that reads images changes the view type when it exists;
                        // provisioning for one that does not is speculative.
                        StreamSpecification = new StreamSpecification
                        {
                            StreamEnabled = true,
                            StreamViewType = StreamViewType.KEYS_ONLY,
                        },
                    },
                    ct);
            }
            catch (ResourceInUseException)
            {
                // Another process won the race. The table exists, which is all this method promises.
            }

            await WaitUntilActiveAsync(ct);
        }

        await SeedCounterAsync(ct);
    }

    // The counter row starts at zero, here, once. Seeding it is what lets the append path carry a
    // single condition shape (current = P) instead of branching between "initialize the counter"
    // and "advance the counter", and a branch on the hottest row in the design is a race waiting
    // to be written. The conditional Put makes the seed idempotent, so a second provisioner and a
    // re-run over an existing table both leave the live counter alone.
    private async Task SeedCounterAsync(CancellationToken ct)
    {
        try
        {
            await _client.PutItemAsync(
                new PutItemRequest
                {
                    TableName = _options.TableName,
                    Item = new()
                    {
                        [DynamoDbSchema.PartitionKeyAttribute] =
                            new AttributeValue { S = DynamoDbSchema.CounterPartitionKey },
                        [DynamoDbSchema.SortKeyAttribute] = new AttributeValue { N = "0" },
                        [DynamoDbSchema.CounterValueAttribute] = new AttributeValue { N = "0" },
                    },
                    ConditionExpression =
                        $"attribute_not_exists({DynamoDbSchema.PartitionKeyAttribute})",
                },
                ct);
        }
        catch (ConditionalCheckFailedException)
        {
            // Already seeded. That is the success case on every run after the first.
        }
    }

    private async Task<bool> TableExistsAsync(CancellationToken ct)
    {
        try
        {
            var described = await _client.DescribeTableAsync(_options.TableName, ct);
            return described.Table.TableStatus == TableStatus.ACTIVE;
        }
        catch (ResourceNotFoundException)
        {
            return false;
        }
    }

    private async Task WaitUntilActiveAsync(CancellationToken ct)
    {
        // CreateTable returns before the table serves reads. Poll DescribeTable rather than the
        // SDK's waiter so the cancellation token governs the wait.
        for (var attempt = 0; attempt < 60; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            if (await TableExistsAsync(ct))
            {
                return;
            }
            await Task.Delay(TimeSpan.FromMilliseconds(250), ct);
        }

        throw new InvalidOperationException(
            $"DynamoDB table '{_options.TableName}' did not become ACTIVE.");
    }
}
