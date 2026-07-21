using EventSourcingCqrs.Domain.Abstractions;

namespace EventSourcingCqrs.Infrastructure.Tests.TestKit;

internal sealed class StubAccessor : ICommandContextAccessor
{
    public ICommandContext? Current { get; set; }
}

internal sealed class StubTenantAccessor : ICurrentTenantAccessor
{
    public TenantId? Current { get; set; }
}

// Reports every event at version 1, the pre-lineage default. These tests build stores without an
// upcaster, so the current version of everything they write is 1.
internal sealed class StubCurrentVersions : ICurrentEventSchemaVersions
{
    public int CurrentVersionFor(string storageName) => 1;
}

internal sealed class StubContext : ICommandContext
{
    public Guid CorrelationId { get; init; } = Guid.Empty;
    public Guid CausationCommandId { get; init; } = Guid.Empty;
    public Guid ActorId { get; init; } = Guid.Empty;
    public IReadOnlyCollection<Role> Roles { get; init; } = [];
    public DispatchAuthorizationMode AuthorizationMode { get; init; }
    public string ServiceName { get; init; } = "TestService";
    public string? IdempotencyKey { get; init; }

    private readonly DateTime _utcNow = new(2026, 5, 11, 12, 0, 0, DateTimeKind.Utc);

    public DateTimeOffset UtcNow() => new(_utcNow, TimeSpan.Zero);
}
