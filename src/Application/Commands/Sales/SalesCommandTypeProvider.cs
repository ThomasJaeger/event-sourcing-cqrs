using EventSourcingCqrs.Domain.Abstractions;

namespace EventSourcingCqrs.Application.Commands.Sales;

// Sales bounded context's user-dispatched commands for HTTP by-name dispatch
// (ADR 0023), parallel to SalesQueryTypeProvider on the query side and feeding
// the same CommandTypeRegistry the delay queue uses (ADR 0017). Canonical order
// mirrors the Order lifecycle: draft, line edits, address, placement, then the
// fulfillment outcomes. The process-manager-dispatched commands are not
// user-initiated and do not register here.
public sealed class SalesCommandTypeProvider : ICommandTypeProvider
{
    public IEnumerable<Type> GetCommandTypes() =>
    [
        typeof(DraftOrder),
        typeof(AddOrderLine),
        typeof(RemoveOrderLine),
        typeof(SetOrderShippingAddress),
        typeof(PlaceOrder),
        typeof(ShipOrder),
        typeof(CancelOrder),
        typeof(MarkOrderCompleted),
    ];
}
