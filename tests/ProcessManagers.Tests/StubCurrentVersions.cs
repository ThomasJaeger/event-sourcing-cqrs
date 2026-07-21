using EventSourcingCqrs.Domain.Abstractions;

namespace EventSourcingCqrs.ProcessManagers.Tests;

// Reports every event at version 1, the pre-lineage default. These harnesses build stores without an
// upcaster, so the current version of everything they write is 1.
internal sealed class StubCurrentVersions : ICurrentEventSchemaVersions
{
    public int CurrentVersionFor(string storageName) => 1;
}
