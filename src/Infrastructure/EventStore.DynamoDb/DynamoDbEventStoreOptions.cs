namespace EventSourcingCqrs.Infrastructure.EventStore.DynamoDb;

// What the adapter needs to reach a DynamoDB table. Unlike every other adapter's options, this
// is not a connection string: DynamoDB is addressed by region, credentials, and a table name,
// and there is no ADO.NET-shaped DSN to parse. The provider-selection guard the hosts run
// (ValidateConnectionString) therefore has no string to parse for this engine. It checks the
// flat keys that carry the service URL and the table name instead, which is what parsing a DSN
// checks for the others.
//
// ServiceUrl is the LocalStack escape hatch and is empty for real AWS, where the SDK resolves
// the endpoint from the region. PLAN.md:470 scopes this phase to LocalStack, so ServiceUrl is
// set on every path the repository currently runs.
public sealed class DynamoDbEventStoreOptions
{
    // The events table. One table holds aggregate rows, PM rows, the position counter, and the
    // log partition: DynamoDB has no cheap cross-table transaction, and the append needs all of
    // them in one TransactWriteItems.
    public string TableName { get; set; } = "event_store_events";

    // Empty means real AWS. LocalStack sets http://localhost:4566 or a mapped container port.
    public string ServiceUrl { get; set; } = string.Empty;

    public string Region { get; set; } = "us-east-1";

    // LocalStack accepts any non-empty pair. Real AWS resolves credentials from its own chain
    // when these are empty, which is what a deployed host would do.
    public string AccessKeyId { get; set; } = string.Empty;

    public string SecretAccessKey { get; set; } = string.Empty;

    // How many times an append re-reads the counter and retries before giving up with
    // DynamoDbPositionContentionException. The default is generous because contention is this
    // engine's normal case: a spike measured one writer retrying 42 times among 12 concurrent
    // appenders, so a small cap would fail correct appends under ordinary load.
    //
    // It is settable because the exhaustion path is otherwise unreachable in a test: driving 64
    // real losses is slow and flaky, and a fact that pins the cap needs to reach it deterministically.
    // A host has no reason to change it.
    public int MaxAppendAttempts { get; set; } = 64;
}
