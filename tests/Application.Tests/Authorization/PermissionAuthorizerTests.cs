using EventSourcingCqrs.Application.Authorization;
using EventSourcingCqrs.Domain.Abstractions;
using FluentAssertions;
using Xunit;

namespace EventSourcingCqrs.Application.Tests.Authorization;

public class PermissionAuthorizerTests
{
    private static PermissionAuthorizer Service() =>
        new(new RolePermissionRegistry(RolePermissionPolicy.Default));

    [Fact]
    public void A_single_role_authorizes_a_permission_it_grants()
    {
        Service().IsAuthorized(new[] { Role.Customer }, Permission.PlaceOrder).Should().BeTrue();
    }

    [Fact]
    public void A_role_does_not_authorize_a_permission_it_does_not_grant()
    {
        Service().IsAuthorized(new[] { Role.Customer }, Permission.RefundPayment).Should().BeFalse();
    }

    [Fact]
    public void Authorization_is_the_union_across_the_principals_roles()
    {
        // Customer grants PlaceOrder; Support grants ProcessReturn. Neither grants the other's.
        var service = Service();
        var roles = new[] { Role.Customer, Role.Support };

        service.IsAuthorized(roles, Permission.PlaceOrder).Should().BeTrue();
        service.IsAuthorized(roles, Permission.ProcessReturn).Should().BeTrue();
    }

    [Fact]
    public void No_roles_authorizes_nothing()
    {
        Service().IsAuthorized(Array.Empty<Role>(), Permission.ViewOrder).Should().BeFalse();
    }

    // Characterization: the RolePermissionPolicy shape pre-exists this test. Admin is computed-all
    // from Enum.GetValues<Permission>(), and the non-Admin roles (Customer, Support, System) are
    // explicit literals, so minting the RebuildProjection member is a no-logic enum addition. These two
    // pin the new member's placement in that existing shape: Admin holds it, a non-Admin role does not.
    [Fact]
    public void Admin_is_authorized_for_RebuildProjection()
    {
        Service().IsAuthorized(new[] { Role.Admin }, Permission.RebuildProjection).Should().BeTrue();
    }

    [Fact]
    public void A_non_admin_role_is_denied_RebuildProjection()
    {
        Service().IsAuthorized(new[] { Role.Support }, Permission.RebuildProjection).Should().BeFalse();
    }
}
