using EventSourcingCqrs.EventStore.ContractTests;
using Xunit;

namespace EventSourcingCqrs.Infrastructure.Tests.Kurrent;

// The KurrentDB backend for the core IEventStore contract suite (docs/TDD_RULES.md §5). Phase 13
// slice 1: the store is a skeleton that throws NotImplementedException, so this is the standing RED
// for the engine; slices 2 through 5 turn its members green one behavior at a time.
//
// It derives the core suite only, neither capability suite:
//   held-writer: KurrentDB has no interactive, held-open append to park mid-transaction.
//   throwing-duplicate: KurrentDB append is idempotent by event id, so it does not raise on a
//     reused id and cannot own the throwing-duplicate fact.
// Both engine reasons are recorded in ADR 0047.
public class KurrentEventStore_Contract_Tests
    : EventStoreContractTests, IClassFixture<KurrentFixture>
{
    private readonly KurrentFixture _fixture;

    public KurrentEventStore_Contract_Tests(KurrentFixture fixture)
    {
        _fixture = fixture;
    }

    protected override async Task<IEventStoreContractBackend> CreateBackendAsync()
        => await KurrentContractBackend.CreateAsync(_fixture);
}
