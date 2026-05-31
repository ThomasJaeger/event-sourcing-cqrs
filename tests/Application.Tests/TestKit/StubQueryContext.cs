using EventSourcingCqrs.Domain.Abstractions;

namespace EventSourcingCqrs.Application.Tests.TestKit;

internal sealed class StubQueryContext : IQueryContext
{
    public Guid ActorId { get; init; } = Guid.Empty;
    public IReadOnlyCollection<Role> Roles { get; init; } = [];
    public bool IsAuthenticatedUserQuery { get; init; }
}
