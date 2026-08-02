namespace EventSourcingCqrs.Infrastructure.EventStore.DynamoDb;

// What the adapter needs to reach a DynamoDB table. Unlike every other adapter's options, this
// is not a connection string: DynamoDB is addressed by region, credentials, and a table name,
// and there is no ADO.NET-shaped DSN to parse. The provider-selection guard the hosts run
// (ValidateConnectionString) therefore has no string to parse for this engine. It checks the
// flat keys that carry the service URL and the table name instead, which is what parsing a DSN
// checks for the others.
//
// ServiceUrl is the LocalStack escape hatch and is empty for real AWS, where the SDK resolves
// the endpoint from the region. PLAN.md's Phase 14 out-of-scope entry for real AWS scopes this phase to LocalStack, so ServiceUrl is
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
    // DynamoDbPositionContentionException. Contention is this engine's normal case: every writer in
    // the deployment races for one counter row, so losing is ordinary, and a cap a correct append
    // can reach under ordinary load converts load into failure.
    //
    // The derivation. One writer wins each round, so among N contenders a given writer loses a round
    // with probability (N-1)/N. At the spike's twelve writers, (11/12)^64 is 3.8e-3: about one
    // append in 262, which over a 300-append run is a two-in-three chance of starving somebody. That
    // is a coin flip, not a cap. (11/12)^256 is 2.1e-10, about one in 4.7 billion, negligible by
    // construction rather than by luck. The model is the worst case, since backoff desynchronizes
    // the writers and stragglers drop out, so real runs starve far less often than it predicts. The
    // ratio is the point: 256 buys eighteen million times the margin, and the extra attempts are
    // only ever spent by an append that was going to fail anyway.
    //
    // Attempts rather than milliseconds, because attempts are what the engine charges for and a
    // wall-clock budget would mean a different cap on every machine. CI proved the hazard: 64
    // survived this repository's 32-core box five runs out of five and starved on a 4-core runner,
    // same code and same LocalStack. What changed was how long a writer waits between reading the
    // counter and writing it, which is scheduling rather than the engine.
    //
    // The cap is backpressure and should stay loud. At 256 an exhaustion is no longer a writer that
    // got unlucky; it is a deployment writing faster than one global position can be handed out, and
    // ADR 0044's ordering guarantee is then the thing to revisit rather than this number.
    //
    // It is settable because the exhaustion path is otherwise unreachable in a test: driving 256
    // real losses is a load test, and a fact that pins the cap needs to reach it deterministically.
    // A host has no reason to change it.
    public int MaxAppendAttempts { get; set; } = 256;
}
