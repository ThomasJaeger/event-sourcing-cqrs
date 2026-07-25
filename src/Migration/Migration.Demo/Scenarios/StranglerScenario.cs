using EventSourcingCqrs.Migration.Demo.LegacyOutbox;
using EventSourcingCqrs.Migration.Demo.Strangler;
using Npgsql;

namespace EventSourcingCqrs.Migration.Demo.Scenarios;

// Strangler: the router sends each order to the event-sourced application or the legacy service by a
// predicate (even ids event-sourced, odd ids legacy), so both implementations run side by side. Ids in
// the 300s: 302 routes event-sourced, 301 routes legacy.
public static class StranglerScenario
{
    private const long EventSourcedOrderId = 302;
    private const long LegacyRoutedOrderId = 301;

    public static async Task RunAsync(DemoContext context)
    {
        Console.WriteLine();
        Console.WriteLine("== Strangler: one order each way, legacy and event-sourced side by side (ids 300s) ==");

        var router = new StranglerRouter(
            new LegacyOrderService(context.LegacyConnectionString, context.EventTypes, context.JsonOptions),
            context.CommandBus);

        await router.PlaceOrderAsync(EventSourcedOrderId, "Ada Lovelace", 42.50m, CancellationToken.None);
        await router.MarkOrderPaidAsync(EventSourcedOrderId, CancellationToken.None);
        await router.PlaceOrderAsync(LegacyRoutedOrderId, "Grace Hopper", 10.00m, CancellationToken.None);
        await router.MarkOrderPaidAsync(LegacyRoutedOrderId, CancellationToken.None);

        Console.WriteLine(
            $"Order {EventSourcedOrderId} (even) routed to the event-sourced application; order {LegacyRoutedOrderId} (odd) routed to the legacy service.");
        await DemoNarration.PrintStreamAsync(context, EventSourcedOrderId, "event-sourced order");

        var (status, total) = await ReadLegacyOrderAsync(context.LegacyConnectionString, LegacyRoutedOrderId);
        Console.WriteLine(
            $"  legacy order (legacy id {LegacyRoutedOrderId}) row: status '{status}', total {total}; its stream stays empty until an emitter drains the outbox.");
    }

    private static async Task<(string Status, decimal Total)> ReadLegacyOrderAsync(
        string connectionString, long orderId)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT status, total FROM legacy.orders WHERE id = @id";
        command.Parameters.AddWithValue("id", orderId);
        await using var reader = await command.ExecuteReaderAsync();
        await reader.ReadAsync();
        return (reader.GetString(0), reader.GetDecimal(1));
    }
}
