using EventSourcingCqrs.Domain.Sales;
using EventSourcingCqrs.Domain.SharedKernel;

namespace EventSourcingCqrs.Application.Queries.Sales;

public sealed record OrderHistoryView(Guid OrderId, IReadOnlyList<OrderHistoryStep> Steps);

public sealed record OrderHistoryStep(
    int Version, DateTime OccurredUtc, string Description, OrderSnapshot Snapshot, Money Total);
