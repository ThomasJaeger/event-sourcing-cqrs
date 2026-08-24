using EventSourcingCqrs.Application.Commands.Sales;
using EventSourcingCqrs.Application.Queries.Sales;
using EventSourcingCqrs.Domain.Sales.Events;
using EventSourcingCqrs.Domain.SharedKernel;

namespace EventSourcingCqrs.Demo.Seeder.Scenarios;

// A saga that cannot proceed, and what the system does about it.
//
// The order here is ordinary. It is drafted, filled and placed exactly as the clean scenario's is,
// and nothing about it is malformed. What stops it is one line whose SKU has no inventory behind
// it. The fulfillment process manager resolves every line's SKU through a projection-private
// lookup before it reserves anything, and a SKU absent from that lookup resolves to nothing. There
// is no failure seam here and no injected fault: an unmapped SKU is ordinary data, and the
// scenario reaches the compensation path through the front door.
//
// What the compensation does, read from the process manager rather than assumed. The payment is
// authorized first, because authorization happens on OrderPlaced and the reservations come after
// it, so there is a real authorization to undo. The fan-out records the line as failed rather than
// reserved, and with no line reserved the compensation has nothing to release. It then voids the
// authorized payment and cancels the order, in that order. Three commands are dispatched in the
// clean case and two here, because the release set is empty.
//
// So the events this leaves behind are PaymentVoided on the payment's stream and OrderCancelled on
// the order's, plus the process manager's own transitions on its stream. No inventory event is
// written at all, which is why no inventory row moves and why the SKU stays absent from every read
// model. That is what makes a fresh SKU per run free here where it would not be in the clean
// scenario: nothing creates inventory for it, so nothing accumulates.
//
// One wait, and it names OrderCancelled deliberately. That is the last event of the compensation a
// projection observes: the process manager's own events follow it and the projection feed excludes
// them. All four projections that subscribe to OrderCancelled handle it, so all four can reach the
// position the wait targets. Naming PaymentVoided instead would derive the order-detail projection
// alone and target a position two events short of the one the run cares about.
public static class CompensationScenario
{
    private const int Quantity = 3;
    private const decimal UnitPrice = 9.99m;

    private static readonly Address ShippingAddress =
        new("500 Terry Francine Street", "South San Francisco", "94080", "US");

    // The same bounds the clean scenario runs, and for the same reasons: the outbox wakes on a
    // notification and falls back to a 500ms idle poll, and the poll interval samples at least
    // twice per worst-case drain cycle.
    private static readonly TimeSpan WaitBudget = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan WaitPollInterval = TimeSpan.FromMilliseconds(250);

    public static async Task RunAsync(SeederContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);

        var customerId = Guid.NewGuid();
        var orderId = Guid.NewGuid();
        var lineId = Guid.NewGuid();

        // Unmapped by construction. Nothing in this scenario creates inventory, so no
        // InventoryCreated ever names this SKU and the lookup the process manager reads stays empty
        // for it however many times the seeder runs.
        var sku = $"UNMAPPED-{orderId.ToString("N")[..8].ToUpperInvariant()}";

        Console.WriteLine();
        Console.WriteLine("== Compensation: an order whose reservation cannot be made ==");
        Console.WriteLine($"  this run's order {orderId} for a fresh customer {customerId}.");
        Console.WriteLine($"  its one line carries {sku}, a SKU no inventory was ever created for.");
        Console.WriteLine(
            $"  the wait is bounded at {WaitBudget.TotalSeconds:N0}s and polls every " +
            $"{WaitPollInterval.TotalMilliseconds:N0}ms. A wait that ends on its bound fails the run.");

        Console.WriteLine();
        Console.WriteLine("  1. Drafting, filling and placing an ordinary order.");
        await context.Commands.SendAsync(new DraftOrder(orderId, customerId), ct);
        var price = new Money(UnitPrice, Currency.USD);
        await context.Commands.SendAsync(
            new AddOrderLine(orderId, lineId, sku, Quantity, price), ct);
        Console.WriteLine($"     line {sku} x{Quantity} at {price}.");
        await context.Commands.SendAsync(new SetOrderShippingAddress(orderId, ShippingAddress), ct);
        await context.Commands.SendAsync(new PlaceOrder(orderId), ct);
        Console.WriteLine("     order placed. Nothing about it is invalid and it will not be fulfilled.");

        Console.WriteLine();
        Console.WriteLine("  2. What the process manager does with it.");
        Console.WriteLine("     it authorizes payment first, because authorization happens on OrderPlaced.");
        Console.WriteLine("     then it resolves each line's SKU through the lookup projection, and finds nothing.");
        Console.WriteLine("     with no line reserved there is nothing to release, so it voids and cancels.");

        // OrderCancelled is the last event of the compensation a projection observes.
        await WaitAsync(context, "the cancelled order", [typeof(OrderCancelled)], ct);

        await NarrateFinalStateAsync(context, orderId, ct);
    }

    // Reads the compensated order back through the same query the UI asks. The timeline is the
    // interesting part: it carries the authorization and the void side by side, which is the whole
    // of what a compensating action means here.
    private static async Task NarrateFinalStateAsync(
        SeederContext context, Guid orderId, CancellationToken ct)
    {
        Console.WriteLine();
        Console.WriteLine("  3. Final state, read back through the query bus.");
        var view = await context.Queries.AskAsync(new GetOrderDetail(orderId), ct)
            ?? throw new InvalidOperationException(
                $"The order detail read model carries no order {orderId}.");

        Console.WriteLine($"     order {view.Header.OrderId} for customer {view.Header.CustomerId}.");
        Console.WriteLine($"     status {view.Header.Status}, total {view.Header.Total}.");
        Console.WriteLine(
            $"     placed {Stamp(view.Header.PlacedUtc)}, cancelled {Stamp(view.Header.CancelledUtc)}.");
        Console.WriteLine($"     timeline carries {view.Timeline.Count} events:");
        foreach (var row in view.Timeline)
        {
            Console.WriteLine($"       {row.GlobalPosition,6}  {row.EventType}");
        }

        Console.WriteLine(
            "     the payment was authorized and then voided, and no inventory event was written at all.");
    }

    private static string Stamp(DateTime? value)
        => value is null ? "never" : value.Value.ToString("u");

    private static async Task WaitAsync(
        SeederContext context, string label, Type[] eventTypes, CancellationToken ct)
    {
        Console.WriteLine($"     waiting on the projections that handle {label}.");
        try
        {
            var names = await context.Waiter.WaitForCatchUpAsync(
                eventTypes, WaitBudget, WaitPollInterval, ct);
            Console.WriteLine($"     caught up: {string.Join(", ", names)}.");
        }
        catch (TimeoutException ex)
        {
            Console.Error.WriteLine(
                $"     the wait on {label} ended on its bound of {WaitBudget.TotalSeconds:N0}s " +
                "rather than on catch up. Nothing below this line ran.");
            Console.Error.WriteLine($"     {ex.Message}");
            throw;
        }
    }
}
