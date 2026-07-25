using EventSourcingCqrs.Application.Commands.Sales;
using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.Domain.SharedKernel;
using EventSourcingCqrs.Migration.Demo.Cdc;
using EventSourcingCqrs.Migration.Demo.LegacyOutbox;

namespace EventSourcingCqrs.Migration.Demo.Strangler;

// Chapter 18: the strangler router. It sends each order to the event-sourced application or the legacy
// CRUD service by a predicate, so the two implementations run side by side and traffic shifts by
// changing the predicate. The event-sourced side composes a placeable draft and places it through the
// command bus; the legacy side writes CRUD rows and outbox events. The ruled mapping: legacy "place" is
// the domain's draft, and legacy "mark paid" is the domain's placement (the paid status the CDC
// translator banks as OrderPlaced).
//
// The predicate is a pure function of the durable legacy id, evaluated once per call, so place and
// mark-paid for one id always land on the same side. A route that varied across the two calls (a coin
// flip, a percentage keyed on wall-clock) would split one order's history across two systems, the one
// thing a strangler must never do.
//
// No idempotency keys here: the bare SendAsync path passes a null key. A production strangler would
// thread a durable key derived from the legacy id through both sides so a retried route does not
// double-apply; the null-key seam is where that key belongs.
public sealed class StranglerRouter
{
    private static readonly Address DemoShippingAddress = new("1 Test St", "Reno", "89501", "US");

    // Default routing: even legacy ids go event-sourced, odd ids stay legacy. A modulo threshold so a
    // demo widens the event-sourced share by changing one predicate.
    public static readonly Func<long, bool> DefaultRoute = id => id % 2 == 0;

    private readonly LegacyOrderService _legacyService;
    private readonly ICommandBus _commandBus;
    private readonly Func<long, bool> _routeToEventSourced;

    public StranglerRouter(
        LegacyOrderService legacyService,
        ICommandBus commandBus,
        Func<long, bool>? routeToEventSourced = null)
    {
        _legacyService = legacyService;
        _commandBus = commandBus;
        _routeToEventSourced = routeToEventSourced ?? DefaultRoute;
    }

    public async Task PlaceOrderAsync(
        long orderId, string customerName, decimal total, CancellationToken cancellationToken)
    {
        if (!_routeToEventSourced(orderId))
        {
            await _legacyService.PlaceOrderAsync(orderId, customerName, total, cancellationToken);
            return;
        }

        // Compose the placeable draft: Order.Place needs a draft with at least one line and a shipping
        // address, so the event-sourced place is the first three commands, and mark-paid places it.
        var eventSourcedId = LegacyChangeTranslator.OrderIdFor(orderId);
        await _commandBus.SendAsync(
            new DraftOrder(eventSourcedId, LegacyChangeTranslator.CustomerIdFor(customerName)),
            cancellationToken);
        await _commandBus.SendAsync(
            new AddOrderLine(eventSourcedId, Guid.NewGuid(), "SKU-001", 1, new Money(total, Currency.USD)),
            cancellationToken);
        await _commandBus.SendAsync(
            new SetOrderShippingAddress(eventSourcedId, DemoShippingAddress),
            cancellationToken);
    }

    public async Task MarkOrderPaidAsync(long orderId, CancellationToken cancellationToken)
    {
        if (!_routeToEventSourced(orderId))
        {
            await _legacyService.MarkOrderPaidAsync(orderId, cancellationToken);
            return;
        }

        await _commandBus.SendAsync(
            new PlaceOrder(LegacyChangeTranslator.OrderIdFor(orderId)), cancellationToken);
    }
}
