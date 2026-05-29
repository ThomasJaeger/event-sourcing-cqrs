using EventSourcingCqrs.Application.Authorization;
using FluentAssertions;
using Xunit;

namespace EventSourcingCqrs.Application.Tests.Authorization;

public class RolePermissionRegistryTests
{
    [Fact]
    public void The_shipped_default_policy_validates_clean()
    {
        var construct = () => new RolePermissionRegistry(RolePermissionPolicy.Default);

        construct.Should().NotThrow();
    }

    [Fact]
    public void A_policy_missing_a_role_entry_throws_at_construction()
    {
        var incomplete = new Dictionary<Role, IReadOnlySet<Permission>>
        {
            [Role.Customer] = new HashSet<Permission> { Permission.ViewOrder },
            [Role.Support] = new HashSet<Permission> { Permission.ViewOrder },
            [Role.Admin] = new HashSet<Permission>(Enum.GetValues<Permission>()),
            // Role.System deliberately omitted.
        };

        var construct = () => new RolePermissionRegistry(incomplete);

        construct.Should().Throw<RolePermissionPolicyException>()
            .WithMessage("*System*");
    }

    [Fact]
    public void A_policy_granting_an_undefined_permission_throws_at_construction()
    {
        var malformed = new Dictionary<Role, IReadOnlySet<Permission>>
        {
            [Role.Customer] = new HashSet<Permission> { (Permission)9999 },
            [Role.Support] = new HashSet<Permission> { Permission.ViewOrder },
            [Role.Admin] = new HashSet<Permission>(Enum.GetValues<Permission>()),
            [Role.System] = new HashSet<Permission> { Permission.ReserveInventory },
        };

        var construct = () => new RolePermissionRegistry(malformed);

        construct.Should().Throw<RolePermissionPolicyException>()
            .WithMessage("*undefined permission*");
    }

    [Fact]
    public void PermissionsFor_returns_the_roles_configured_set()
    {
        var registry = new RolePermissionRegistry(RolePermissionPolicy.Default);

        registry.PermissionsFor(Role.Customer).Should().Contain(Permission.PlaceOrder);
        registry.PermissionsFor(Role.Customer).Should().NotContain(Permission.RefundPayment);
    }

    [Fact]
    public void Admin_holds_every_permission()
    {
        var registry = new RolePermissionRegistry(RolePermissionPolicy.Default);

        registry.PermissionsFor(Role.Admin).Should().BeEquivalentTo(Enum.GetValues<Permission>());
    }
}
