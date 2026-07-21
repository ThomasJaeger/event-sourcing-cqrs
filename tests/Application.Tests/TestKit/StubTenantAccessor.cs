using EventSourcingCqrs.Domain.Abstractions;

namespace EventSourcingCqrs.Application.Tests.TestKit;

internal sealed class StubTenantAccessor : ICurrentTenantAccessor
{
    public TenantId? Current { get; set; }
}

// Reports every event at version 1, the pre-lineage default. These fixtures build stores without an
// upcaster, so the current version of everything they write is 1.
internal sealed class StubCurrentVersions : ICurrentEventSchemaVersions
{
    public int CurrentVersionFor(string storageName) => 1;
}
