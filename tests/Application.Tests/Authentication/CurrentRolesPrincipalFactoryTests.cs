using EventSourcingCqrs.Application.Authentication;
using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.Domain.Access.ReadModels;
using FluentAssertions;
using Xunit;

namespace EventSourcingCqrs.Application.Tests.Authentication;

public class CurrentRolesPrincipalFactoryTests
{
    [Fact]
    public async Task CreateAsync_loads_the_actors_roles_from_the_current_roles_read_model()
    {
        var actorId = Guid.NewGuid();
        var store = new StubCurrentUserRolesStore([Role.Customer, Role.Support]);
        var factory = new CurrentRolesPrincipalFactory(store);

        var principal = await factory.CreateAsync(actorId, CancellationToken.None);

        principal.ActorId.Should().Be(actorId);
        principal.Roles.Should().BeEquivalentTo(new[] { Role.Customer, Role.Support });
        // The roles are the read model's, keyed on the actor, not a forwarded claim.
        store.QueriedUserId.Should().Be(actorId);
    }

    [Fact]
    public async Task CreateAsync_returns_an_empty_role_set_when_the_actor_holds_none()
    {
        var factory = new CurrentRolesPrincipalFactory(new StubCurrentUserRolesStore([]));

        var principal = await factory.CreateAsync(Guid.NewGuid(), CancellationToken.None);

        principal.Roles.Should().BeEmpty();
    }

    // Reads only; the factory never writes, so the unit-of-work members throw if reached.
    private sealed class StubCurrentUserRolesStore : ICurrentUserRolesStore
    {
        private readonly IReadOnlyCollection<Role> _roles;

        public StubCurrentUserRolesStore(IReadOnlyCollection<Role> roles) => _roles = roles;

        public Guid? QueriedUserId { get; private set; }

        public Task<IReadOnlyCollection<Role>> GetRolesForUserAsync(Guid userId, CancellationToken ct)
        {
            QueriedUserId = userId;
            return Task.FromResult(_roles);
        }

        public Task<ICurrentUserRolesUnitOfWork> BeginAsync(CancellationToken ct)
            => throw new NotSupportedException("The principal factory reads roles; it does not write.");

        public Task TruncateAsync(CancellationToken ct)
            => throw new NotSupportedException("The principal factory reads roles; it does not write.");
    }
}
