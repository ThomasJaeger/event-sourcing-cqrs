using EventSourcingCqrs.Application.Commands.Fulfillment;
using EventSourcingCqrs.Application.Commands.Sales;
using EventSourcingCqrs.Application.Queries.Fulfillment;
using EventSourcingCqrs.Application.Queries.Sales;
using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.Domain.Fulfillment.Events;
using EventSourcingCqrs.Domain.SharedKernel;

namespace EventSourcingCqrs.Demo.Seeder.Scenarios;

// One order id, two tenants, two different orders.
//
// Nothing provisions a tenant in this system. There is no tenant table, no tenant aggregate and no
// configuration key that names one: a tenant is a value carried on a dispatch and stamped onto
// every stream, every event and every read-model row from there. So a second tenant needs no setup
// beyond deciding on its id.
//
// What it does need is a principal. The only dispatch overload that carries a tenant is the one
// that also carries an actor and its roles, and that overload sets the authorization mode to
// AuthenticatedUser, which the authorization behavior enforces. The bare overloads used by the
// other two scenarios pass through unenforced and always send the default tenant. So this scenario
// is the first to authenticate, and it runs as an Admin, whose permission set is computed from the
// whole enumeration. Admin also holds ViewCustomer, which makes the read side treat it as
// operational rather than owner-scoped, so what each read returns is decided by the tenant alone
// and not by an ownership filter.
//
// Both legs go through the same overload, and the default tenant is passed explicitly rather than
// left to the bare path. That is the point of the shape: the two legs are the same code with one
// argument different.
//
// Inventory is per tenant and has to be. The SKU lookup the process manager reads is keyed on
// (tenant_id, sku) and its query filters on both, so a tenant that never created inventory resolves
// its SKU to nothing however much of that SKU another tenant holds. Both tenants therefore stock
// the same SKU string and each gets its own inventory aggregate behind it, which is the case the
// composite key exists to allow.
//
// The waits are unchanged in mechanism because checkpoints are global. The checkpoint table is
// keyed on the projection name alone and carries no tenant column, which ADR 0039 records as
// correct: lag is a property of the projection worker rather than of a tenant. So a projection
// advances on either tenant's events and one wait covers both legs.
public static class TenantsScenario
{
    // A fixed second tenant, so repeated runs accumulate under one tenant rather than inventing a
    // new one each time. The default tenant is the other leg.
    private static readonly TenantId SecondTenant =
        TenantId.From(Guid.Parse("00000000-0000-0000-0000-0000000000a2"));

    // Admin, because its permission set is every permission and the scenario dispatches across
    // Sales and Fulfillment both. A narrower role would need the set widened per command for no
    // gain to what this demonstrates.
    private static readonly IReadOnlyCollection<Role> AdminOnly = [Role.Admin];

    private const string SharedSku = "TENANT-DEMO-WIDGET";
    private const int OpeningStock = 100;
    private const int Quantity = 4;
    private const decimal UnitPrice = 7.25m;

    private static readonly Address ShippingAddress =
        new("500 Terry Francine Street", "South San Francisco", "94080", "US");

    private static readonly TimeSpan WaitBudget = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan WaitPollInterval = TimeSpan.FromMilliseconds(250);

    public static async Task RunAsync(SeederContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);

        // The one identifier both tenants use. Fresh per run so repeated runs do not collide with
        // each other, shared across the two legs so the run demonstrates what it claims to.
        var sharedOrderId = Guid.NewGuid();

        var legs = new[]
        {
            new TenantLeg("tenant one, the default", WellKnownTenants.Default),
            new TenantLeg("tenant two", SecondTenant),
        };

        Console.WriteLine();
        Console.WriteLine("== Two tenants: one order id, two orders that never meet ==");
        Console.WriteLine($"  both tenants will use order id {sharedOrderId}.");
        Console.WriteLine($"  both will stock the SKU {SharedSku}, each behind its own inventory.");
        Console.WriteLine(
            $"  every wait is bounded at {WaitBudget.TotalSeconds:N0}s and polls every " +
            $"{WaitPollInterval.TotalMilliseconds:N0}ms. A wait that ends on its bound fails the run.");

        Console.WriteLine();
        Console.WriteLine("  1. Stocking the same SKU under each tenant.");
        foreach (var leg in legs)
        {
            var existing = await context.Queries.AskAsync(
                new GetInventoryDashboardBySku(SharedSku), leg.ActorId, AdminOnly, leg.Tenant, ct);

            if (existing is null)
            {
                await SendAsync(context, new CreateInventory(leg.InventoryId, SharedSku), leg, ct);
                await SendAsync(
                    context,
                    new AdjustInventory(leg.InventoryId, OpeningStock, "Opening stock for the demo."),
                    leg, ct);
                Console.WriteLine(
                    $"     {leg.Label}: inventory {leg.InventoryId} created for {SharedSku}, stocked to {OpeningStock}.");
            }
            else
            {
                await SendAsync(
                    context,
                    new AdjustInventory(existing.InventoryId, Quantity, "Top up for another demo run."),
                    leg, ct);
                Console.WriteLine(
                    $"     {leg.Label}: inventory {existing.InventoryId} already holds {SharedSku} " +
                    $"at on hand {existing.OnHandQuantity}. Topped up by {Quantity}.");
            }
        }

        // InventoryAdjusted is the last inventory event either branch writes under either tenant, so
        // the dashboard is the projection that can reach the feed head. The SKU lookup handles only
        // InventoryCreated and stops short of it; the dashboard reaching the head settles it too.
        await WaitAsync(context, "the stocked inventory under both tenants", [typeof(InventoryAdjusted)], ct);

        Console.WriteLine();
        Console.WriteLine("  2. Placing an order under each tenant, on the same order id.");
        foreach (var leg in legs)
        {
            await SendAsync(context, new DraftOrder(sharedOrderId, leg.CustomerId), leg, ct);
            await SendAsync(
                context,
                new AddOrderLine(
                    sharedOrderId, leg.LineId, SharedSku, Quantity, new Money(UnitPrice, Currency.USD)),
                leg, ct);
            await SendAsync(context, new SetOrderShippingAddress(sharedOrderId, ShippingAddress), leg, ct);
            await SendAsync(context, new PlaceOrder(sharedOrderId), leg, ct);
            Console.WriteLine(
                $"     {leg.Label}: order {sharedOrderId} placed for customer {leg.CustomerId}.");
        }

        Console.WriteLine("     the same id was accepted twice because the stream carries the tenant.");

        // The process manager runs each order to a scheduled shipment. ShipmentScheduled is the last
        // event of that chain a projection observes, and the two projections that handle it both
        // reach the feed head. The scenario stops there rather than dispatching and delivering,
        // because what it demonstrates is isolation and not the rest of the lifecycle.
        await WaitAsync(context, "both placed orders and their fulfillment", [typeof(ShipmentScheduled)], ct);

        await ReadBackUnderEachTenantAsync(context, legs, sharedOrderId, ct);
    }

    // The demonstration. Each tenant asks for the same order id and gets its own order, and neither
    // can see the other's. The two reads differ in exactly one argument.
    private static async Task ReadBackUnderEachTenantAsync(
        SeederContext context, TenantLeg[] legs, Guid sharedOrderId, CancellationToken ct)
    {
        Console.WriteLine();
        Console.WriteLine("  3. Reading the same order id back under each tenant.");
        foreach (var leg in legs)
        {
            var view = await context.Queries.AskAsync(
                new GetOrderDetail(sharedOrderId), leg.ActorId, AdminOnly, leg.Tenant, ct)
                ?? throw new InvalidOperationException(
                    $"{leg.Label} sees no order {sharedOrderId}, which this scenario just placed for it.");

            var other = legs.Single(l => l.Tenant != leg.Tenant);
            Console.WriteLine($"     {leg.Label} asked for order {sharedOrderId} and sees:");
            Console.WriteLine($"       customer {view.Header.CustomerId}, status {view.Header.Status}, total {view.Header.Total}.");
            Console.WriteLine($"       it does not see customer {other.CustomerId}, which is the other tenant's.");

            // A page rather than a total: ListOrders takes an offset and a limit and the store
            // clamps an oversized limit internally, so this reports what the first page holds.
            var orders = await context.Queries.AskAsync(
                new ListOrders(0, 100), leg.ActorId, AdminOnly, leg.Tenant, ct);
            Console.WriteLine($"       the first page of its order list carries {orders.Count} row(s).");

            var dashboard = await context.Queries.AskAsync(
                new GetAllInventoryDashboard(), leg.ActorId, AdminOnly, leg.Tenant, ct);
            Console.WriteLine($"       its inventory dashboard carries {dashboard.Count} row(s):");
            foreach (var row in dashboard)
            {
                Console.WriteLine(
                    $"         {row.Sku,-20} inventory {row.InventoryId}  on hand {row.OnHandQuantity,5}" +
                    $"  reserved {row.ReservedQuantity,5}");
            }
        }

        Console.WriteLine();
        Console.WriteLine(
            "     one order id, two customers, two inventories behind one SKU string. Neither read " +
            "reached the other tenant's rows.");
    }

    private static Task SendAsync(
        SeederContext context, ICommand command, TenantLeg leg, CancellationToken ct)
        // The tenant-carrying overload, which is also the authenticated one. The idempotency key is
        // null, which behaves exactly as the bare path does.
        => context.Commands.SendAsync(command, leg.ActorId, AdminOnly, leg.Tenant, null, ct);

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

    // One tenant's side of the run. The actor, customer, line and inventory ids are minted per leg
    // per run, so the only thing the two legs share is the order id and the SKU, which is what the
    // scenario is about.
    private sealed record TenantLeg(string Label, TenantId Tenant)
    {
        public Guid ActorId { get; } = Guid.NewGuid();
        public Guid CustomerId { get; } = Guid.NewGuid();
        public Guid LineId { get; } = Guid.NewGuid();
        public Guid InventoryId { get; } = Guid.NewGuid();
    }
}
