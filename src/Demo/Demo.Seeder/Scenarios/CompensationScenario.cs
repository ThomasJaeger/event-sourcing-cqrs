using EventSourcingCqrs.Application.Commands.Sales;
using EventSourcingCqrs.Application.Queries.Sales;
using EventSourcingCqrs.Domain.Billing.Events;
using EventSourcingCqrs.Domain.Sales;
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
// reserved, and with no line reserved the compensation has nothing to release. It cancels the
// order before voiding the authorization, fencing further fulfillment first (ADR 0055).
//
// So the events this leaves behind are PaymentVoided on the payment's stream and OrderCancelled on
// the order's, plus the process manager's own transitions on its stream. No inventory event is
// written at all, which is why no inventory row moves and why the SKU stays absent from every read
// model. That is what makes a fresh SKU per run free here where it would not be in the clean
// scenario: nothing creates inventory for it, so nothing accumulates.
//
// Wait for this order's cancelled state and payment void in its detail timeline. A global feed
// head is not a workflow-completion condition: Sales projections stop at OrderCancelled while
// PaymentVoided advances that head, and catching up to OrderPlaced says nothing about compensation.
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
        Console.WriteLine("     with no line reserved there is nothing to release, so it cancels and voids.");

        await WaitForCompensationAsync(context, orderId, ct);

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

    private static async Task WaitForCompensationAsync(
        SeederContext context, Guid orderId, CancellationToken ct)
    {
        Console.WriteLine("     waiting for this order's cancellation and payment void.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(WaitBudget);
        try
        {
            while (true)
            {
                deadline.Token.ThrowIfCancellationRequested();
                var view = await context.Queries.AskAsync(new GetOrderDetail(orderId), deadline.Token);
                if (view?.Header.Status == OrderStatus.Cancelled
                    && view.Timeline.Any(row => row.EventType == nameof(PaymentVoided)))
                {
                    Console.WriteLine("     compensation observed: the order is cancelled and its payment is voided.");
                    return;
                }
                await Task.Delay(WaitPollInterval, deadline.Token);
            }
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested && deadline.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"Order {orderId} did not reach cancelled state with a voided payment within {WaitBudget}.", ex);
        }
    }
}
