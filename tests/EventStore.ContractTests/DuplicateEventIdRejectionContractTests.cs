using EventSourcingCqrs.Domain.Abstractions;
using FluentAssertions;
using Xunit;

namespace EventSourcingCqrs.EventStore.ContractTests;

// The duplicate-event-id rejection capability probe (docs/TDD_RULES.md §5). The universal suite
// pins only that a reused event id is never reported as a concurrency conflict, because a
// silently-idempotent engine satisfies nothing stronger. An engine whose store raises on a
// duplicate event id owes the stronger claim pinned here, and derives this suite to prove it. The
// backend seam is the universal one: the fact needs only Store, and the capability is expressed by
// which engines derive the class rather than by a wider hook.
public abstract class DuplicateEventIdRejectionContractTests
{
    // One backend per fact, isolated from every other. The adapter's test project supplies it.
    protected abstract Task<IEventStoreContractBackend> CreateBackendAsync();

    [Fact]
    public async Task Appending_a_duplicate_event_id_propagates_untranslated()
    {
        await using var backend = await CreateBackendAsync();
        var streamA = ContractEnvelopes.NewStreamId();
        var streamB = ContractEnvelopes.NewStreamId();
        var sharedEventId = Guid.NewGuid();

        await backend.Store.AppendAsync(streamA, 0,
            [
                ContractEnvelopes.Build(
                    streamA, 1, new ContractOrderPlaced(Guid.NewGuid(), 10m), sharedEventId),
            ],
            CancellationToken.None);

        // Append is not idempotent by event id and does not dedupe. A reused event id is a
        // programming bug, not a retry-and-replay condition, so it must not arrive dressed as
        // ConcurrencyException and must not be silently swallowed. The concrete exception type
        // is engine-owned, so the suite pins the strongest thing every engine owes: it throws,
        // it is not the concurrency contract, and the append did not land.
        var collision = ContractEnvelopes.Build(
            streamB, 1, new ContractOrderNoted("collision"), sharedEventId);
        var act = async () =>
            await backend.Store.AppendAsync(streamB, 0, [collision], CancellationToken.None);

        var thrown = await act.Should().ThrowAsync<Exception>();
        thrown.Which.Should().NotBeOfType<ConcurrencyException>();

        (await backend.Store.ReadStreamAsync(streamB, 0, CancellationToken.None))
            .Should().BeEmpty();
    }
}
