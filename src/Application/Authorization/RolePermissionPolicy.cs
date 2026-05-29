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

        var system = new HashSet<Permission>
        {
            Permission.ReserveInventory,
            Permission.ReleaseInventory,
            Permission.AuthorizePayment,
            Permission.CapturePayment,
            Permission.RefundPayment,
            Permission.ScheduleShipment,
            Permission.DispatchShipment,
            Permission.MarkOrderCompleted,
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
