using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.Domain.Access;
using EventSourcingCqrs.Domain.Access.Events;
using EventSourcingCqrs.Domain.Tests.TestKit;
using FluentAssertions;
using Xunit;

namespace EventSourcingCqrs.Domain.Tests.Access;

public class UserRolesTests
{
    private static readonly Guid UserId = Guid.Parse("33333333-3333-3333-3333-333333333333");

    [Fact]
    public void Create_opens_the_stream_and_grants_the_first_role()
    {
        var userRoles = UserRoles.Create(UserId, Role.Admin);

        userRoles.Id.Should().Be(UserId);
        userRoles.Roles.Should().BeEquivalentTo(new[] { Role.Admin });
        userRoles.DequeueUncommittedEvents()
            .Should().ContainSingle()
            .Which.Should().BeEquivalentTo(new RoleAssigned(UserId, Role.Admin));
    }

    [Fact]
    public void Create_throws_for_an_empty_user_id()
    {
        var action = () => UserRoles.Create(Guid.Empty, Role.Admin);

        action.Should().Throw<DomainException>().WithMessage("*empty user id*");
    }

    [Fact]
    public void Assign_grants_a_second_role()
    {
        new AggregateTest<UserRoles>()
            .Given(new RoleAssigned(UserId, Role.Customer))
            .When(u => u.Assign(Role.Support))
            .Then(new RoleAssigned(UserId, Role.Support));
    }

    [Fact]
    public void Assign_throws_when_the_role_is_already_held()
    {
        new AggregateTest<UserRoles>()
            .Given(new RoleAssigned(UserId, Role.Customer))
            .When(u => u.Assign(Role.Customer))
            .ThenThrows<DomainException>();
    }

    [Fact]
    public void Revoke_removes_a_held_role()
    {
        new AggregateTest<UserRoles>()
            .Given(new RoleAssigned(UserId, Role.Customer), new RoleAssigned(UserId, Role.Support))
            .When(u => u.Revoke(Role.Support))
            .Then(new RoleRevoked(UserId, Role.Support));
    }

    [Fact]
    public void Revoke_throws_when_the_role_is_not_held()
    {
        new AggregateTest<UserRoles>()
            .Given(new RoleAssigned(UserId, Role.Customer))
            .When(u => u.Revoke(Role.Admin))
            .ThenThrows<DomainException>();
    }

    [Fact]
    public void Rehydration_reflects_assignments_and_revocations()
    {
        var userRoles = new UserRoles();
        userRoles.ApplyHistoric(new RoleAssigned(UserId, Role.Customer));
        userRoles.ApplyHistoric(new RoleAssigned(UserId, Role.Support));
        userRoles.ApplyHistoric(new RoleRevoked(UserId, Role.Customer));

        userRoles.Id.Should().Be(UserId);
        userRoles.Roles.Should().BeEquivalentTo(new[] { Role.Support });
    }
}
