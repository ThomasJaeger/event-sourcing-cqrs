using EventSourcingCqrs.Application.Authorization;
using EventSourcingCqrs.Domain.Abstractions;
using FluentAssertions;
using Xunit;

namespace EventSourcingCqrs.Application.Tests.Authorization;

public class RolePermissionPolicyTests
{
    private static readonly IReadOnlyDictionary<Role, IReadOnlySet<Permission>> Policy =
        RolePermissionPolicy.Default;

    [Fact]
    public void Customer_can_draft_an_order()
    {
        Policy[Role.Customer].Should().Contain(Permission.DraftOrder);
    }

    [Fact]
    public void The_system_role_holds_exactly_the_caused_dispatch_permissions()
    {
        // The invariant: the System role's set equals the commands a process manager or a compensation
        // routine dispatches under the system actor. CapturePayment, RefundPayment, and DispatchShipment
        // are deliberately absent (none is caused); CancelOrder, VoidPayment, and AdjustInventory are
        // present (the compensation dispatches).
        Policy[Role.System].Should().BeEquivalentTo(new[]
        {
            Permission.AuthorizePayment,
            Permission.ReserveInventory,
            Permission.ReleaseInventory,
            Permission.ScheduleShipment,
            Permission.MarkOrderCompleted,
            Permission.CancelOrder,
            Permission.VoidPayment,
            Permission.AdjustInventory,
        });
    }

    [Fact]
    public void Admin_holds_every_defined_permission()
    {
        Policy[Role.Admin].Should().BeEquivalentTo(Enum.GetValues<Permission>());
    }

    [Fact]
    public void AccessAdminConsole_is_granted_to_Admin_and_to_no_other_role()
    {
        // ADR 0040: AccessAdminConsole is the operator-console access capability. Admin holds it through
        // the computed-from-enumeration grant; Customer, Support, and System must not, so console access
        // stays an operator capability.
        Policy[Role.Admin].Should().Contain(Permission.AccessAdminConsole);
        Policy[Role.Customer].Should().NotContain(Permission.AccessAdminConsole);
        Policy[Role.Support].Should().NotContain(Permission.AccessAdminConsole);
        Policy[Role.System].Should().NotContain(Permission.AccessAdminConsole);
    }

    [Fact]
    public void Refund_payment_stays_out_of_support()
    {
        Policy[Role.Support].Should().NotContain(Permission.RefundPayment);
    }
}
