using EventSourcingCqrs.Migration.Demo.Cdc;
using Npgsql;

namespace EventSourcingCqrs.Migration.Demo.Scenarios;

// CDC: plain legacy CRUD writes fire the change-tracking trigger, and the reader turns those change rows
// into domain events. Ids in the 100s.
public static class CdcScenario
{
    private const long PaidOrderId = 101;
    private const long DraftedOrderId = 102;

    public static async Task RunAsync(DemoContext context)
    {
        Console.WriteLine();
        Console.WriteLine("== CDC: legacy change-tracking into domain events (ids 100s) ==");

        await SeedLegacyWritesAsync(context.LegacyConnectionString);
        var changeCount = await CountChangesAsync(context.LegacyConnectionString);
        Console.WriteLine(
            $"Legacy CRUD: inserted order {PaidOrderId} (new), updated it to paid, inserted order {DraftedOrderId} (new).");
        Console.WriteLine($"That produced {changeCount} rows in legacy.legacy_changes.");

        await new CdcReader(context.LegacyConnectionString, context.EventStore, context.SchemaVersions)
            .RunAsync(CancellationToken.None);

        Console.WriteLine("The CDC reader consumed the pending change rows and appended these events:");
        await DemoNarration.PrintStreamAsync(context, PaidOrderId, "paid order");
        await DemoNarration.PrintStreamAsync(context, DraftedOrderId, "drafted order");
    }

    private static async Task SeedLegacyWritesAsync(string connectionString)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await ExecuteAsync(
            connection,
            "INSERT INTO legacy.orders (id, customer_name, status, total) VALUES (@id, 'Ada Lovelace', 'new', 42.50)",
            PaidOrderId);
        await ExecuteAsync(connection, "UPDATE legacy.orders SET status = 'paid' WHERE id = @id", PaidOrderId);
        await ExecuteAsync(
            connection,
            "INSERT INTO legacy.orders (id, customer_name, status, total) VALUES (@id, 'Grace Hopper', 'new', 10.00)",
            DraftedOrderId);
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql, long orderId)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("id", orderId);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<long> CountChangesAsync(string connectionString)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM legacy.legacy_changes";
        return (long)(await command.ExecuteScalarAsync())!;
    }
}
