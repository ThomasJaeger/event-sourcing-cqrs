using EventSourcingCqrs.Domain.Abstractions;

namespace EventSourcingCqrs.Application.Authorization;

// The static role-to-permission policy. The policy is part of the application definition,
// expressed in code rather than external configuration, so there is one explicit source with no
// per-host duplication and no silent default. It changes with deployments. The registry validates
// it at composition.
public static class RolePermissionPolicy
{
    public static IReadOnlyDictionary<Role, IReadOnlySet<Permission>> Default { get; } = Build();

    private static IReadOnlyDictionary<Role, IReadOnlySet<Permission>> Build()
    {
        var customer = new HashSet<Permission>
        {
            Permission.PlaceOrder,
            Permission.DraftOrder,
            Permission.ViewOrder,
            Permission.CancelOrder,
            Permission.ManageOrderLines,
        };

        var support = new HashSet<Permission>
        {
            Permission.ViewOrder,
            Permission.ViewCustomer,
            Permission.ViewInventory,
            Permission.ViewShipment,
            Permission.ProcessReturn,
        };

        // The System role's set equals the caused-dispatch command set exactly (P9.4): the commands a
        // process manager or a compensation routine dispatches under the system actor. Least privilege,
        // so it drops CapturePayment (dispatched by nothing in v1), RefundPayment (user-only), and
        // DispatchShipment (user-only), and adds the compensation dispatches CancelOrder, VoidPayment,
        // and AdjustInventory. Enforcement does not touch the caused path yet, so this pre-stages the
        // caused-command commit with no P9.4 runtime effect.
        var system = new HashSet<Permission>
        {
            Permission.AuthorizePayment,
            Permission.ReserveInventory,
            Permission.ReleaseInventory,
            Permission.ScheduleShipment,
            Permission.MarkOrderCompleted,
            Permission.CancelOrder,
            Permission.VoidPayment,
            Permission.AdjustInventory,
        };

        // Admin holds every permission by definition, computed from the enumeration so the
        // invariant cannot drift as the permission set grows.
        var admin = new HashSet<Permission>(Enum.GetValues<Permission>());

        return new Dictionary<Role, IReadOnlySet<Permission>>
        {
            [Role.Customer] = customer,
            [Role.Support] = support,
            [Role.Admin] = admin,
            [Role.System] = system,
        };
    }
}
