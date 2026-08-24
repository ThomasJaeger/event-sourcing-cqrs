using System.Text.Json;
using EventSourcingCqrs.Application.Commands.Fulfillment;
using EventSourcingCqrs.Application.Commands.Sales;
using EventSourcingCqrs.Application.Queries.Fulfillment;
using EventSourcingCqrs.Application.Queries.Sales;
using EventSourcingCqrs.Domain.Fulfillment.Events;
using EventSourcingCqrs.Domain.Sales.Events;
using EventSourcingCqrs.Domain.SharedKernel;

namespace EventSourcingCqrs.Demo.Seeder.Scenarios;

// The happy path, end to end: a catalogue is stocked, an order is drafted and placed, the
// fulfillment process manager runs it to a scheduled shipment, and the shipment is dispatched and
// delivered until the order completes. Everything goes through the same command bus and query bus
// a host dispatches through, so the scenario demonstrates the system rather than a shortcut into
// its database.
//
// What is fresh per run and what is not. The order, its customer and its line ids are minted on
// every run, so running the seeder twice adds a second order rather than colliding with the first.
// The catalogue is the opposite: SKU and inventory id are a fixture the runs share, because both
// SKU-keyed read models are uniquely keyed on the SKU within a tenant and both write through
// ON CONFLICT DO NOTHING. A run that minted a fresh inventory id for an existing SKU would leave
// the lookup pointing at the first inventory while stocking the second, so reservations would
// resolve to an aggregate this run never stocked. Fresh SKUs avoid that and cost something else:
// the dashboard carries one row per SKU with no bound, so a reader who ran the seeder ten times
// would meet thirty of them.
//
// So the catalogue is created once and topped up thereafter. GetInventoryDashboardBySku answers
// whether a SKU already has inventory and carries the inventory id when it does, which is what
// makes the guard possible without the seeder keeping state of its own. The top-up adds exactly
// what this run will reserve, so available stock holds steady while on hand and reserved both
// climb, which is what a reader should see from a system that never releases a delivered order's
// reservation.
//
// The ids the process manager mints, the payment and the shipment, are read back out of the
// order's timeline. Guessing them is not possible and no read model maps an order to its shipment.
//
// Why each wait names one event type rather than the set written. A projection checkpoints only on
// events it handles, and the waiter compares every derived projection against the head of the feed.
// Name a type whose projections do not handle the last event written and one of them stops short
// of that head, and the wait can only end on its bound. So each wait names the last event of its
// leg that a projection observes. The outbox drains in insertion order and runs every handler for a
// message before moving on, so a projection reaching the head also settles every earlier event.
public static class CleanOrderScenario
{
    // The shared catalogue. Inventory id is fixed beside the SKU because the two are one to one for
    // the SKU's lifetime, which is the invariant the lookup's conflict clause states.
    private static readonly CatalogueItem[] Catalogue =
    [
        new("NOTEBOOK", Guid.Parse("33333333-3333-3333-3333-333333333301"), 2, 12.50m),
        new("PENCIL", Guid.Parse("33333333-3333-3333-3333-333333333302"), 10, 2.25m),
        new("STAPLER", Guid.Parse("33333333-3333-3333-3333-333333333303"), 1, 18.00m),
    ];

    private const int OpeningStock = 100;

    private static readonly Address ShippingAddress =
        new("500 Terry Francine Street", "South San Francisco", "94080", "US");

    // Unchanged from the run that proved them. The outbox wakes on a notification and falls back to
    // a 500ms idle poll, so a leg is a small number of drains and lands in low single-digit seconds.
    // Thirty seconds leaves room for a cold pool and a first-call JIT without leaving a stopped
    // Workers host looking like a slow one.
    private static readonly TimeSpan WaitBudget = TimeSpan.FromSeconds(30);

    // Half the outbox's idle fallback, so the waiter samples at least twice per worst-case drain
    // cycle. Each poll is one checkpoint read per derived projection plus one head read.
    private static readonly TimeSpan WaitPollInterval = TimeSpan.FromMilliseconds(250);

    public static async Task RunAsync(SeederContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);

        var run = RunIdentifiers.Mint();

        Console.WriteLine();
        Console.WriteLine("== Clean order: one order from draft through to completion ==");
        Console.WriteLine($"  this run's order {run.OrderId} for a fresh customer {run.CustomerId}.");
        Console.WriteLine(
            $"  every wait is bounded at {WaitBudget.TotalSeconds:N0}s and polls every " +
            $"{WaitPollInterval.TotalMilliseconds:N0}ms. A wait that ends on its bound fails the run.");

        await StockTheCatalogueAsync(context, run, ct);
        await PlaceTheOrderAsync(context, run, ct);
        var shipmentId = await ReadShipmentIdFromTheTimelineAsync(context, run, ct);
        await CarryTheShipmentAsync(context, run, shipmentId, ct);
        await NarrateFinalStateAsync(context, run, ct);
    }

    // Demonstrates that inventory is an aggregate like any other and that a read model can answer
    // whether it already exists. The stock has to be there before the order is placed, because the
    // process manager resolves each line's SKU through a projection-private lookup that the
    // InventoryCreated events feed. An order placed before that lookup catches up reserves nothing
    // and compensates, which is the other scenario rather than this one.
    private static async Task StockTheCatalogueAsync(
        SeederContext context, RunIdentifiers run, CancellationToken ct)
    {
        Console.WriteLine();
        Console.WriteLine("  1. Stocking the catalogue.");
        foreach (var item in Catalogue)
        {
            var existing = await context.Queries.AskAsync(
                new GetInventoryDashboardBySku(item.Sku), ct);

            if (existing is null)
            {
                await context.Commands.SendAsync(new CreateInventory(item.InventoryId, item.Sku), ct);
                await context.Commands.SendAsync(
                    new AdjustInventory(item.InventoryId, OpeningStock, "Opening stock for the demo."), ct);
                Console.WriteLine($"     {item.Sku}: inventory created and stocked to {OpeningStock}.");
            }
            else
            {
                await context.Commands.SendAsync(
                    new AdjustInventory(
                        existing.InventoryId, item.Quantity, "Top up for another demo run."), ct);
                Console.WriteLine(
                    $"     {item.Sku}: inventory already exists at on hand {existing.OnHandQuantity}, " +
                    $"reserved {existing.ReservedQuantity}. Topped up by {item.Quantity}.");
            }
        }

        // InventoryAdjusted is the last inventory event either branch writes, so the dashboard is
        // the projection that can reach the feed head. The SKU lookup handles only InventoryCreated
        // and stops short of it, which is why it is not named here even though it is the lookup the
        // order depends on. The dashboard reaching the head settles the lookup's earlier events too.
        await WaitAsync(context, "the stocked inventory", [typeof(InventoryAdjusted)], ct);
    }

    // Demonstrates the Sales write path and the invariants guarding it: lines and a shipping address
    // are required before an order can be placed, and the aggregate rejects the placement otherwise.
    // OrderPlaced is what starts the fulfillment process manager, so this is also where the
    // asynchronous half of the system takes over.
    private static async Task PlaceTheOrderAsync(
        SeederContext context, RunIdentifiers run, CancellationToken ct)
    {
        Console.WriteLine();
        Console.WriteLine("  2. Drafting, filling and placing the order.");
        await context.Commands.SendAsync(new DraftOrder(run.OrderId, run.CustomerId), ct);
        for (var i = 0; i < Catalogue.Length; i++)
        {
            var item = Catalogue[i];
            var price = new Money(item.UnitPrice, Currency.USD);
            await context.Commands.SendAsync(
                new AddOrderLine(run.OrderId, run.LineIds[i], item.Sku, item.Quantity, price), ct);
            Console.WriteLine($"     line {item.Sku} x{item.Quantity} at {price}.");
        }

        await context.Commands.SendAsync(
            new SetOrderShippingAddress(run.OrderId, ShippingAddress), ct);
        await context.Commands.SendAsync(new PlaceOrder(run.OrderId), ct);
        Console.WriteLine("     order placed. The fulfillment process manager takes it from here.");

        // The process manager authorizes payment, reserves every line, and asks for a shipment. The
        // shipment being scheduled is the last event of that chain a projection observes.
        await WaitAsync(context, "the placed order and its fulfillment", [typeof(ShipmentScheduled)], ct);
    }

    // Demonstrates why a read side exists. The process manager minted the shipment id itself and no
    // command carried it back, so the only way to reach it is the order's timeline, where every
    // observed event is recorded with its payload.
    private static async Task<Guid> ReadShipmentIdFromTheTimelineAsync(
        SeederContext context, RunIdentifiers run, CancellationToken ct)
    {
        Console.WriteLine();
        Console.WriteLine("  3. Reading the minted shipment id back off the order's timeline.");
        var view = await context.Queries.AskAsync(new GetOrderDetail(run.OrderId), ct)
            ?? throw new InvalidOperationException(
                $"The order detail read model carries no order {run.OrderId}.");

        Console.WriteLine(
            "     timeline: " + string.Join(", ", view.Timeline.Select(row => row.EventType)) + ".");

        var scheduled = OrderDetailTimelineReader.ReadFirst<ShipmentScheduled>(
            view, nameof(ShipmentScheduled), context.JsonOptions);
        Console.WriteLine($"     shipment {scheduled.ShipmentId} was minted by the process manager.");
        return scheduled.ShipmentId;
    }

    // Demonstrates the Fulfillment write path closing the workflow. Both commands are ordinary
    // aggregate writes; the process manager observes their events and marks the order completed
    // without the seeder asking it to.
    private static async Task CarryTheShipmentAsync(
        SeederContext context, RunIdentifiers run, Guid shipmentId, CancellationToken ct)
    {
        Console.WriteLine();
        Console.WriteLine("  4. Dispatching and delivering the shipment.");
        var carrierReference = $"DEMO-CARRIER-{run.OrderId.ToString("N")[..8]}";
        await context.Commands.SendAsync(new DispatchShipment(shipmentId, carrierReference), ct);
        Console.WriteLine($"     dispatched under carrier reference {carrierReference}.");
        await context.Commands.SendAsync(new DeliverShipment(shipmentId), ct);
        Console.WriteLine("     delivered. The process manager should now complete the order.");

        // OrderCompleted is the last event of the workflow a projection observes. The process
        // manager writes its own events after it, and those never reach a projection, which is why
        // this wait is held against the feed head rather than the tail of the log.
        await WaitAsync(context, "the delivered shipment and the completed order", [typeof(OrderCompleted)], ct);
    }

    // Reads the finished order back through the same query the UI asks, so what the scenario claims
    // to have produced is what a reader can go and look at.
    private static async Task NarrateFinalStateAsync(
        SeederContext context, RunIdentifiers run, CancellationToken ct)
    {
        Console.WriteLine();
        Console.WriteLine("  5. Final state, read back through the query bus.");
        var view = await context.Queries.AskAsync(new GetOrderDetail(run.OrderId), ct)
            ?? throw new InvalidOperationException(
                $"The order detail read model carries no order {run.OrderId}.");

        Console.WriteLine($"     order {view.Header.OrderId} for customer {view.Header.CustomerId}.");
        Console.WriteLine($"     status {view.Header.Status}, total {view.Header.Total}.");
        Console.WriteLine(
            $"     placed {Stamp(view.Header.PlacedUtc)}, completed {Stamp(view.Header.CompletedUtc)}.");
        foreach (var line in view.Lines)
        {
            Console.WriteLine($"     line {line.Sku} x{line.Quantity} at {line.UnitPrice}.");
        }

        Console.WriteLine($"     timeline carries {view.Timeline.Count} events:");
        foreach (var row in view.Timeline)
        {
            Console.WriteLine($"       {row.GlobalPosition,6}  {row.EventType}");
        }

        var dashboard = await context.Queries.AskAsync(new GetAllInventoryDashboard(), ct);
        Console.WriteLine($"     inventory dashboard carries {dashboard.Count} rows:");
        foreach (var row in dashboard)
        {
            Console.WriteLine(
                $"       {row.Sku,-10} on hand {row.OnHandQuantity,5}   reserved {row.ReservedQuantity,5}" +
                $"   available {row.OnHandQuantity - row.ReservedQuantity,5}");
        }
    }

    private static string Stamp(DateTime? value)
        => value is null ? "never" : value.Value.ToString("u");

    // One wait, narrated. The waiter derives the projections from the event types and returns the
    // set it waited on, so the narration names what was waited for rather than what was hoped for.
    // A bound that expires is reported as the bound expiring and then rethrown, because a wait that
    // ran out has observed nothing and must never read as a leg that finished.
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

    private sealed record CatalogueItem(string Sku, Guid InventoryId, int Quantity, decimal UnitPrice);

    // Everything this run creates anew. Minted once at the top so every step of the run names the
    // same order, and so two runs never collide.
    private sealed record RunIdentifiers(Guid CustomerId, Guid OrderId, Guid[] LineIds)
    {
        public static RunIdentifiers Mint()
            => new(
                Guid.NewGuid(),
                Guid.NewGuid(),
                [.. Catalogue.Select(_ => Guid.NewGuid())]);
    }
}
