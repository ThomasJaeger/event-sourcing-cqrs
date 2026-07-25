using EventSourcingCqrs.Migration.Demo.LegacyOutbox;
using EventSourcingCqrs.Migration.Demo.Shadow;
using Npgsql;

namespace EventSourcingCqrs.Migration.Demo.Scenarios;

// Shadow mode: the shadow service does the authoritative legacy write and emits the parallel events; the
// comparator checks the two agree. Then a deliberate legacy-only change makes them diverge, so the reader
// sees both verdicts. Ids in the 400s.
public static class ShadowScenario
{
    private const long OrderId = 401;

    public static async Task RunAsync(DemoContext context)
    {
        Console.WriteLine();
        Console.WriteLine("== Shadow mode: parallel emission compared for correctness (ids 400s) ==");

        var shadow = new ShadowOrderService(
            new LegacyOrderService(context.LegacyConnectionString, context.EventTypes, context.JsonOptions),
            new EventStreamAppender(context.EventStore, context.SchemaVersions),
            context.LegacyConnectionString);

        await shadow.PlaceOrderAsync(OrderId, "Ada Lovelace", 42.50m, CancellationToken.None);
        await shadow.MarkOrderPaidAsync(OrderId, CancellationToken.None);
        Console.WriteLine($"Shadow service wrote legacy order {OrderId} and emitted its events in parallel.");
        await DemoNarration.PrintStreamAsync(context, OrderId, "order");

        var agree = ShadowComparator.Compare(
            await ReadLegacyStateAsync(context.LegacyConnectionString, OrderId),
            await DemoNarration.ReadStreamAsync(context, OrderId));
        Console.WriteLine($"  comparator verdict: {Describe(agree)}");

        await DivergeLegacyAsync(context.LegacyConnectionString, OrderId);
        Console.WriteLine($"Then a legacy-only change set order {OrderId} to 'cancelled' with no matching event.");
        var diverged = ShadowComparator.Compare(
            await ReadLegacyStateAsync(context.LegacyConnectionString, OrderId),
            await DemoNarration.ReadStreamAsync(context, OrderId));
        Console.WriteLine($"  comparator verdict: {Describe(diverged)}");
    }

    private static string Describe(ShadowComparisonResult result)
        => result.IsMatch ? "match" : $"mismatch, {result.Detail}";

    private static async Task<LegacyOrderState> ReadLegacyStateAsync(string connectionString, long orderId)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, customer_name, status, total FROM legacy.orders WHERE id = @id";
        command.Parameters.AddWithValue("id", orderId);
        await using var reader = await command.ExecuteReaderAsync();
        await reader.ReadAsync();
        return new LegacyOrderState(
            reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetDecimal(3));
    }

    private static async Task DivergeLegacyAsync(string connectionString, long orderId)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE legacy.orders SET status = 'cancelled' WHERE id = @id";
        command.Parameters.AddWithValue("id", orderId);
        await command.ExecuteNonQueryAsync();
    }
}
