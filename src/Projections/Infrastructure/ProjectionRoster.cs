using EventSourcingCqrs.Domain.Abstractions;

namespace EventSourcingCqrs.Projections.Infrastructure;

// The name-only projection roster: the eight projection identities as their
// checkpoint names, sourced from the ProjectionNames constants. Carries no
// projection instance, store, or serializer, so an operator host composes it
// without the read-model registration's over-provisioning. ProjectionRosterCoverageTests
// pins these names equal to the registered IProjection set, so a projection added
// to the registration without a roster entry fails the build.
internal sealed class ProjectionRoster : IProjectionRoster
{
    public IReadOnlyCollection<string> Names { get; } =
    [
        ProjectionNames.OrderList,
        ProjectionNames.SkuToInventoryId,
        ProjectionNames.OrderIdToPaymentId,
        ProjectionNames.CustomerSummary,
        ProjectionNames.InventoryDashboard,
        ProjectionNames.OrderThroughput,
        ProjectionNames.OrderDetail,
        ProjectionNames.CurrentRoles,
    ];
}
