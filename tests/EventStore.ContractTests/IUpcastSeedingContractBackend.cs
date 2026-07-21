using EventSourcingCqrs.Domain.Abstractions;

namespace EventSourcingCqrs.EventStore.ContractTests;

// The seam the upcast-on-read suite needs on top of the universal backend: a way to plant a
// historical row. Production append only ever writes the current shape, because it derives the
// stored event type from the payload's registered name and only the terminal shape is registered,
// so a version-1 row of an older shape cannot come through it. A backend seeds one the way an older
// revision of the code would have written it: the payload serialized as-is, stored under the
// terminal's stable storage name at schema version 1. Every engine that carries the read-time
// upcaster implements this, the way engines with an interactive append implement IHeldWriterContractBackend.
public interface IUpcastSeedingContractBackend : IEventStoreContractBackend
{
    // Plants one aggregate event on its own stream: versionOnePayload serialized as it stands,
    // stored under terminalStorageName at schema version 1.
    Task SeedVersionOneAsync(
        StreamId stream, string terminalStorageName, IDomainEvent versionOnePayload);
}
