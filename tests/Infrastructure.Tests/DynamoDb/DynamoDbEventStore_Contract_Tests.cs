using EventSourcingCqrs.EventStore.ContractTests;
using Xunit;

namespace EventSourcingCqrs.Infrastructure.Tests.DynamoDb;

// The DynamoDB adapter against the shared contract suite (docs/TDD_RULES.md §5), on a real engine
// through LocalStack.
//
// Phase 14 slice 1: the store is a skeleton whose members throw NotImplementedException, so this is
// the standing RED for the engine. Later slices turn its members green one behavior at a time.
//
// It derives two of the three capability classes, and which two is an engine fact rather than a
// preference:
//
//   held-writer: not derived. DynamoDB has no interactive, held-open write to park mid-flight. A
//     spike against the live engine found AmazonDynamoDBClient's only transaction surface is
//     TransactWriteItems, a one-shot request resolved server-side from its whole item list, with no
//     Begin, Commit, or Rollback on the client at all. ClientRequestToken exists but is an
//     idempotency window, not a held transaction. A backend whose held writer is a no-op would make
//     those probes vacuous, so this engine does not derive them, and a concurrent-append load probe
//     stands in for the ordering evidence they would have carried (slice 2, following the KurrentDB
//     precedent).
//
//   throwing-duplicate: derived. The engine has no opinion about a reused event id on its own, but
//     the adapter's append writes a per-event-id condition row inside the same transaction, so a
//     reused id fails the append rather than landing twice. The condition is the adapter's to make,
//     which is why this engine owns the fact where KurrentDB, whose append is idempotent by event
//     id, cannot.
//
// Both engine reasons are recorded in ADR 0049.
public class DynamoDbEventStore_Contract_Tests : EventStoreContractTests, IClassFixture<LocalStackFixture>
{
    private readonly LocalStackFixture _fixture;

    public DynamoDbEventStore_Contract_Tests(LocalStackFixture fixture) => _fixture = fixture;

    protected override async Task<IEventStoreContractBackend> CreateBackendAsync()
        => await DynamoDbContractBackend.CreateAsync(_fixture);
}

// Each fact gets its own table on the shared node, so this class is parallel-safe with the core
// suite above.
public class DynamoDbEventStore_DuplicateEventId_Contract_Tests
    : DuplicateEventIdRejectionContractTests, IClassFixture<LocalStackFixture>
{
    private readonly LocalStackFixture _fixture;

    public DynamoDbEventStore_DuplicateEventId_Contract_Tests(LocalStackFixture fixture)
        => _fixture = fixture;

    protected override async Task<IEventStoreContractBackend> CreateBackendAsync()
        => await DynamoDbContractBackend.CreateAsync(_fixture);
}
