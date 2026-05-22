using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.Domain.Fulfillment.Events;
using EventSourcingCqrs.Domain.Fulfillment.ReadModels;

namespace EventSourcingCqrs.Projections.SkuToInventoryId;

// A SKU-to-InventoryId lookup over the Fulfillment context's InventoryCreated
// events, populated for the OrderFulfillment process manager to resolve a line's
// SKU to the Inventory aggregate it reserves against (commit 22). Structural
// template is OrderListProjection: open a unit of work, read the checkpoint in
// the same transaction, skip if the event's position is at or below it, make the
// one read-model write, and commit, advancing the checkpoint atomically.
public sealed class SkuToInventoryIdProjection
    : IProjection,
      IEventHandler<InventoryCreated>
{
    public string Name => "sku-to-inventory-id";

    private readonly ISkuToInventoryIdStore _store;

    public SkuToInventoryIdProjection(ISkuToInventoryIdStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        _store = store;
    }

    public async Task HandleAsync(EventContext<InventoryCreated> context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        await using var uow = await _store.BeginAsync(ct);
        if (await uow.GetCheckpointAsync(Name, ct) >= context.GlobalPosition)
        {
            // At-least-once redelivery of an already-processed event. The uow
            // disposes without commit, rolling the empty transaction back.
            return;
        }
        await uow.RecordAsync(context.Event.Sku, context.Event.InventoryId, ct);
        await uow.CommitAsync(Name, context.GlobalPosition, ct);
    }
}
