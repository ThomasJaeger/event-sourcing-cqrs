using EventSourcingCqrs.Domain.Abstractions;
using FluentAssertions;
using Xunit;

namespace EventSourcingCqrs.EventStore.ContractTests;

// Upcast-on-read across all four engines (Chapter 11: Upcasting). Every fact here is a
// green-on-write characterization, and its provenance is the reason it carries no RED of its own:
// the mechanism it pins was RED-proven already, at the V1 unit level in EventUpcasterPipelineTests
// and at the V2a composition level in EventUpcasterPipelineCompositionTests. What is new is not the
// pipeline but its threading through each store's read path, landed in V2a; these facts pin that
// threading end to end against a real backend, and each was green the moment it was written. Their
// teeth are proven the way §3's anti-theater demands of a characterization: by temporarily bypassing
// the Upcast/ResolveType wiring in each store and watching them fail, then restoring it.
//
// A backend plants the version-1 row through IUpcastSeedingContractBackend rather than through the
// store, because production append cannot write an older shape: it stamps the payload's registered
// name and only the terminal is registered. The seed reproduces a historical write; the store then
// reads it back through the real pipeline.
public abstract class UpcastOnReadContractTests
{
    protected abstract Task<IUpcastSeedingContractBackend> CreateBackendAsync();

    [Fact]
    public async Task ReadStreamAsync_lifts_a_version_one_event_to_the_current_shape()
    {
        await using var backend = await CreateBackendAsync();
        var stream = ContractEnvelopes.NewStreamId();
        var itemId = Guid.NewGuid();
        await backend.SeedVersionOneAsync(
            stream, nameof(ContractItemArchived), new ContractItemArchivedV1(itemId));

        var read = await backend.Store.ReadStreamAsync(stream, 0, CancellationToken.None);

        // The stored shape was version 1; what comes back is the terminal, the upcaster's default
        // applied to the added member, and the envelope stamped at the current version.
        read.Should().ContainSingle();
        var upcast = read[0].Payload.Should().BeOfType<ContractItemArchived>().Which;
        upcast.ItemId.Should().Be(itemId);
        upcast.Revision.Should().Be(1);
        read[0].EventVersion.Should().Be(2);
    }

    [Fact]
    public async Task ReadAllAsync_surfaces_a_version_one_event_already_lifted()
    {
        await using var backend = await CreateBackendAsync();
        var stream = ContractEnvelopes.NewStreamId();
        var itemId = Guid.NewGuid();
        await backend.SeedVersionOneAsync(
            stream, nameof(ContractItemArchived), new ContractItemArchivedV1(itemId));

        var feed = new List<EventEnvelope>();
        await foreach (var envelope in backend.Store.ReadAllAsync(0, CancellationToken.None))
        {
            feed.Add(envelope);
        }

        // The global feed a projection drives off carries the current shape too, not the raw stored
        // one, so a projection never sees a version it was not written against.
        var row = feed.Should().ContainSingle().Which;
        var upcast = row.Payload.Should().BeOfType<ContractItemArchived>().Which;
        upcast.ItemId.Should().Be(itemId);
        upcast.Revision.Should().Be(1);
        row.EventVersion.Should().Be(2);
    }
}
