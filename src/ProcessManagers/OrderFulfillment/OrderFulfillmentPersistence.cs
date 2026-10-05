using EventSourcingCqrs.Domain.Abstractions;

namespace EventSourcingCqrs.ProcessManagers.OrderFulfillment;

internal static class OrderFulfillmentPersistence
{
    // Chapter 10: persist resumable per-line progress before the narrowest event
    // store's append limit. DynamoDB permits 33 events; keep headroom for a
    // neighboring transition. Terminal transitions are saved by the caller.
    private const int BatchSize = 32;

    public static Task SaveBatchIfFullAsync(OrderFulfillmentProcessManager pm,
        IProcessManagerRepository<OrderFulfillmentProcessManager> repository, CancellationToken ct)
        => pm.GetUncommittedEvents().Count >= BatchSize
            ? repository.SaveAsync(pm, ct)
            : Task.CompletedTask;
}
