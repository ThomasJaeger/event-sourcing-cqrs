using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.Domain.Access.Events;
using EventSourcingCqrs.Projections.CurrentRoles;
using FluentAssertions;
using Xunit;

namespace EventSourcingCqrs.Projections.Tests;

public class CurrentRolesProjectionTests
{
    private static readonly DateTime At = new(2026, 5, 29, 9, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task RoleAssigned_records_the_user_role_and_advances_the_checkpoint()
    {
        var store = new InMemoryCurrentUserRolesStore();
        var projection = new CurrentRolesProjection(store);
        var userId = Guid.NewGuid();

        await projection.HandleAsync(
            Context(new RoleAssigned(userId, Role.Admin), position: 1), CancellationToken.None);

        (await store.GetRolesForUserAsync(userId, CancellationToken.None))
            .Should().BeEquivalentTo(new[] { Role.Admin });
        store.Checkpoints[projection.Name].Should().Be(1);
    }

    [Fact]
    public async Task RoleRevoked_removes_the_user_role()
    {
        var store = new InMemoryCurrentUserRolesStore();
        var projection = new CurrentRolesProjection(store);
        var userId = Guid.NewGuid();

        await projection.HandleAsync(
            Context(new RoleAssigned(userId, Role.Support), position: 1), CancellationToken.None);
        await projection.HandleAsync(
            Context(new RoleRevoked(userId, Role.Support), position: 2), CancellationToken.None);

        (await store.GetRolesForUserAsync(userId, CancellationToken.None)).Should().BeEmpty();
    }

    [Fact]
    public async Task Multiple_roles_accumulate_for_one_user()
    {
        var store = new InMemoryCurrentUserRolesStore();
        var projection = new CurrentRolesProjection(store);
        var userId = Guid.NewGuid();

        await projection.HandleAsync(
            Context(new RoleAssigned(userId, Role.Customer), position: 1), CancellationToken.None);
        await projection.HandleAsync(
            Context(new RoleAssigned(userId, Role.Support), position: 2), CancellationToken.None);

        (await store.GetRolesForUserAsync(userId, CancellationToken.None))
            .Should().BeEquivalentTo(new[] { Role.Customer, Role.Support });
    }

    [Fact]
    public async Task RoleAssigned_skips_when_position_is_at_or_below_checkpoint()
    {
        var store = new InMemoryCurrentUserRolesStore();
        var projection = new CurrentRolesProjection(store);
        var userId = Guid.NewGuid();

        await projection.HandleAsync(
            Context(new RoleAssigned(userId, Role.Customer), position: 10), CancellationToken.None);
        // A redelivery at position 10 (at the checkpoint) returns early, so the second assignment
        // does not apply.
        await projection.HandleAsync(
            Context(new RoleAssigned(userId, Role.Admin), position: 10), CancellationToken.None);

        (await store.GetRolesForUserAsync(userId, CancellationToken.None))
            .Should().BeEquivalentTo(new[] { Role.Customer });
    }

    private static EventContext<TEvent> Context<TEvent>(TEvent @event, long position)
        where TEvent : IDomainEvent
        => new(@event, Metadata(), position);

    private static EventMetadata Metadata()
        => new(
            EventId: Guid.NewGuid(),
            CorrelationId: Guid.NewGuid(),
            CausationId: Guid.NewGuid(),
            ActorId: Guid.Empty,
            Source: "test",
            SchemaVersion: 1,
            OccurredUtc: At,
            Tenant: WellKnownTenants.Default);
}
