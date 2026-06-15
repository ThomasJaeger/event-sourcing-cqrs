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
    public void Admin_holds_every_permission_including_the_six_new_command_permissions()
    {
        Policy[Role.Admin].Should().BeEquivalentTo(Enum.GetValues<Permission>());
        Policy[Role.Admin].Should().HaveCount(23);
    }

    [Fact]
    public void Refund_payment_stays_out_of_support()
    {
        Policy[Role.Support].Should().NotContain(Permission.RefundPayment);
    }
}
