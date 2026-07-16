using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.Runtime;

namespace EventSourcingCqrs.Infrastructure.Tests.DynamoDb;

// A client that serves the counter read and then cancels every transaction, failing one chosen
// item index. It exists because the adapter's translation is pure logic over a positional reason
// array, and aiming the real engine at a given index is either slow or impossible: an id-row
// failure needs a second writer racing on the same event id, and the attempt cap needs enough real
// losses to be a load test rather than a fact.
//
// TDD_RULES forbids mocking what you do not own, and this is worth naming rather than hiding. This
// is not a stand-in for the engine: every claim about what DynamoDB does is pinned against
// LocalStack in the storm probe and the contract suite. What this fake stands in for is the shape
// of a reason array the spike already measured, so that the adapter's response to it can be pinned
// without a race. The engine facts stay engine-tested; the branch logic gets a deterministic pin.
//
// It inherits AmazonDynamoDBClient rather than implementing IAmazonDynamoDB: the interface carries
// well over a hundred members, and the two the adapter calls are the two overridden here. Any other
// member the adapter starts calling reaches the base client, which has no endpoint and fails
// loudly, which is the behavior this wants.
internal sealed class FakeCancellingDynamoDb : AmazonDynamoDBClient
{
    private readonly int _failedIndex;
    private readonly int _itemCount;

    public int TransactAttempts { get; private set; }

    public FakeCancellingDynamoDb(int failedIndex, int itemCount)
        : base(
            new BasicAWSCredentials("fake", "fake"),
            new AmazonDynamoDBConfig { ServiceURL = "http://localhost:1", AuthenticationRegion = "us-east-1" })
    {
        _failedIndex = failedIndex;
        _itemCount = itemCount;
    }

    // The counter row, always at zero. The adapter reads it before every attempt.
    public override Task<GetItemResponse> GetItemAsync(
        GetItemRequest request, CancellationToken cancellationToken = default)
        => Task.FromResult(new GetItemResponse
        {
            Item = new()
            {
                ["pk"] = new AttributeValue { S = "$counter" },
                ["sk"] = new AttributeValue { N = "0" },
                ["current"] = new AttributeValue { N = "0" },
            },
        });

    // Cancels with one item failed, the rest reported None, which is the shape the live engine
    // returns: a reason per item, positionally, with None for the items that were fine.
    public override Task<TransactWriteItemsResponse> TransactWriteItemsAsync(
        TransactWriteItemsRequest request, CancellationToken cancellationToken = default)
    {
        TransactAttempts++;
        var reasons = Enumerable.Range(0, _itemCount)
            .Select(i => i == _failedIndex
                ? new CancellationReason { Code = "ConditionalCheckFailed", Message = "The conditional request failed" }
                : new CancellationReason { Code = "None" })
            .ToList();

        throw new TransactionCanceledException("Transaction cancelled, please refer cancellation reasons for specific reasons")
        {
            CancellationReasons = reasons,
        };
    }
}
